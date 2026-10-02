"""Local-only mod workspace. Start with: python lab/server.py [--no-open]."""
import argparse
import base64
import hashlib
import io
import json
import math
import mimetypes
import os
import re
import secrets
import subprocess
import sys
import threading
import time
import uuid
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, unquote, urlencode, urlparse
from xml.etree import ElementTree as ET

GAME = Path(os.environ.get("LAB_GAME", r"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"))
HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
STATIC = HERE / "static"
PWSH = os.environ.get("LAB_PWSH", "pwsh")
TOKEN = secrets.token_urlsafe(32)
LOCK = threading.RLock()
JOBS = {}
ACTIVE = None
ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")
SUITES = {
    "sim": {"title": "Mod logic", "detail": "Real game data + mod code. No game launch.", "engine": False},
    "static": {"title": "Game compatibility", "detail": "Check DLL targets and XML patches. No game launch.", "engine": False},
    "chain": {"title": "All offline checks", "detail": "Build, archives, logic, UI and compatibility.", "engine": False},
    "probe": {"title": "Inside the engine", "detail": "Launch an isolated headless server; test container routing.", "engine": True},
    "engine": {"title": "World baseline", "detail": "Launch an isolated headless server; check mods, world and logs.", "engine": True},
    "scenario": {"title": "Custom scenario", "detail": "Build your own console assertions in the browser.", "engine": True},
}
PREVIEW_TARGETS = {
    "party": {"title": "Party portrait", "flag": "--template", "target": "party_entry"},
    "menu": {"title": "Main menu / MODS button", "flag": "--window", "target": "mainMenu"},
}


def scratch():
    common = subprocess.check_output(
        ["git", "-C", str(ROOT), "rev-parse", "--path-format=absolute", "--git-common-dir"], text=True).strip()
    return Path(common).parent / ".scratch"


def workspace():
    home = scratch() / "weblab"
    home.mkdir(parents=True, exist_ok=True)
    return home


def atomic_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps(data, ensure_ascii=False, allow_nan=False), encoding="utf-8")
    temp.replace(path)


def safe_child(base, name):
    path = (base / name).resolve()
    if not path.is_relative_to(base.resolve()):
        raise ValueError("Path is outside the allowed folder")
    return path


def check_results(output):
    results = []
    for line in ANSI.sub("", output).splitlines():
        match = re.match(r"^\s*(?:\[SimProbe\]\s*)?(PASS|FAIL|SKIP)\s+(.+)", line)
        if match:
            status = match[1].lower()
            if status == "pass" and "skipped" in match[2].lower():
                status = "skip"
            results.append({"status": status, "label": match[2]})
    return results


def snapshot(job):
    return {**job, "checks": check_results(job.get("output", ""))}


def job_list():
    with LOCK:
        return [snapshot(j) for j in sorted(JOBS.values(), key=lambda j: j["started"], reverse=True)[:40]]


def load_jobs():
    for path in (workspace() / "jobs").glob("*.json"):
        try:
            job = json.loads(path.read_text(encoding="utf-8"))
            if job["status"] == "running":
                job.update(status="interrupted", finished=time.time())
                job["output"] += "\nLab server stopped before this result was recorded.\n"
            JOBS[job["id"]] = job
        except (ValueError, KeyError, OSError):
            continue


def command_for(kind, params, job_id):
    if kind in ("sim", "static", "chain"):
        script = {"sim": "Test-Sim.ps1", "static": "Test-Coop.ps1", "chain": "test.ps1"}[kind]
        return [PWSH, "-NoProfile", "-File", str(ROOT / "scripts" / script)], 600, None
    if kind in ("probe", "engine"):
        return [sys.executable, "-u", str(ROOT / "tools" / "headless.py"), "probe" if kind == "probe" else "run"], 900, None
    if kind == "scenario":
        scenario = validate_scenario(params)
        path = workspace() / "scenarios" / (job_id + ".json")
        atomic_json(path, scenario)
        return [sys.executable, "-u", str(ROOT / "tools" / "headless.py"), "run", "--scenario", str(path)], 900, None
    if kind == "preview":
        target = PREVIEW_TARGETS.get(params.get("target", "party"))
        state = params.get("state", "healthy")
        if not target or state not in ("healthy", "low", "dead", "muted", "far"):
            raise ValueError("Unknown preview or player state")
        values = params.get("values", {})
        if not isinstance(values, dict) or any(not isinstance(v, (str, int, float, bool)) for v in values.values()):
            raise ValueError("Preview values must be simple JSON fields")
        folder = workspace() / "previews"
        folder.mkdir(exist_ok=True)
        value_path = folder / (job_id + ".json")
        atomic_json(value_path, values)
        out = folder / (job_id + ".png")
        command = [sys.executable, "-u", str(ROOT / "tools" / "xui-preview.py"), target["flag"], target["target"],
                   "--state", state, "--values", str(value_path), "--out", str(out), "--size", "2"]
        return command, 60, out
    raise ValueError("Unknown test suite")


def start_job(kind, params=None):
    global ACTIVE
    with LOCK:
        if ACTIVE:
            raise RuntimeError("Another job is running; wait for it to finish")
        if params is not None and not isinstance(params, dict):
            raise ValueError("Job parameters must be a JSON object")
        job_id = uuid.uuid4().hex
        command, timeout, artifact = command_for(kind, params or {}, job_id)
        job = {"id": job_id, "kind": kind, "params": params or {}, "started": time.time(),
               "finished": None, "status": "running", "output": "", "exitCode": None, "artifact": None}
        JOBS[job_id] = job
        ACTIVE = job_id
        atomic_json(workspace() / "jobs" / (job_id + ".json"), job)
        threading.Thread(target=execute_job, args=(job, command, timeout, artifact), daemon=True).start()
        return snapshot(job)


def execute_job(job, command, timeout, artifact):
    global ACTIVE
    process = None
    expired = threading.Event()
    timer = None
    try:
        env = {**os.environ, "PYTHONUNBUFFERED": "1", "PYTHONIOENCODING": "utf-8"}
        process = subprocess.Popen(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, encoding="utf-8", errors="replace", env=env)

        def expire():
            expired.set()
            if process.poll() is None:
                # Kill this job's process tree only, never other lab/game processes.
                if os.name == "nt":
                    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
                else:
                    process.kill()

        timer = threading.Timer(timeout, expire)
        timer.start()
        for line in process.stdout:
            with LOCK:
                job["output"] = (job["output"] + ANSI.sub("", line))[-250000:]
        code = process.wait()
        with LOCK:
            failed_checks = any(c["status"] == "fail" for c in check_results(job["output"]))
            job.update(exitCode=code, status="timeout" if expired.is_set() else "passed" if code == 0 and not failed_checks else "failed")
            if expired.is_set():
                job["output"] += "\nJob timed out. Inspect engine logs if a server run was interrupted.\n"
            if code == 0 and artifact and artifact.exists():
                job["artifact"] = "/file?" + urlencode({"path": str(artifact.relative_to(scratch()))})
    except Exception as error:
        with LOCK:
            job.update(status="failed", output=job["output"] + "\n" + str(error))
    finally:
        if timer:
            timer.cancel()
        if process and process.stdout:
            process.stdout.close()
        with LOCK:
            job["finished"] = time.time()
            atomic_json(workspace() / "jobs" / (job["id"] + ".json"), job)
            ACTIVE = None


def validate_scenario(data):
    """A small read-only assertion surface, not a shell/console proxy."""
    queries = {"hr doctor", "pois", "gettime", "getgamestats", "listplayers", "version"}
    name = data.get("name", "Custom browser scenario")
    steps = data.get("steps")
    if not isinstance(name, str) or not 1 <= len(name) <= 100:
        raise ValueError("Give the scenario a short name")
    if not isinstance(steps, list) or not 1 <= len(steps) <= 20:
        raise ValueError("Use between 1 and 20 assertion steps")
    checked = [{"expect_clean": "server"}]
    for step in steps:
        if not isinstance(step, dict) or set(step) != {"expect_console"}:
            raise ValueError("Browser scenarios support console assertions only")
        spec = step["expect_console"]
        if not isinstance(spec, dict) or spec.get("tel") not in queries:
            raise ValueError("Choose a supported read-only console query")
        pattern = spec.get("pattern")
        if not isinstance(pattern, str) or not 1 <= len(pattern) <= 250:
            raise ValueError("Expected-output pattern must be 1–250 characters")
        try:
            re.compile(pattern)
        except re.error as error:
            raise ValueError("Invalid expected-output pattern: " + str(error)) from error
        checked.append({"expect_console": {"tel": spec["tel"], "pattern": pattern, "forbid": bool(spec.get("forbid")), "wait": 4}})
    checked.append({"expect_clean": "server"})
    return {"name": name, "steps": checked}


def previews():
    paths = sorted(scratch().rglob("*.png"), key=lambda p: p.stat().st_mtime, reverse=True)
    return [{"name": p.name, "path": str(p.relative_to(scratch())),
             "url": "/file?" + urlencode({"path": str(p.relative_to(scratch()))}),
             "modified": p.stat().st_mtime} for p in paths[:300]]


def world(name):
    folder = safe_child(GAME / "Data" / "Worlds", name)
    if not (folder / "biomes.png").is_file():
        raise FileNotFoundError("No biome map for this world")
    from PIL import Image
    with Image.open(folder / "biomes.png") as image:
        dimensions = list(image.size)
    properties = {p.get("name"): p.get("value") for p in ET.parse(folder / "map_info.xml").getroot().findall("property")}
    size = [int(v) for v in properties["HeightMapSize"].split(",")]
    spawns, prefabs = [], []
    if (folder / "spawnpoints.xml").exists():
        spawns = [[float(v) for v in p.get("position").split(",")] for p in ET.parse(folder / "spawnpoints.xml").getroot()]
    if (folder / "prefabs.xml").exists():
        for p in ET.parse(folder / "prefabs.xml").getroot():
            if p.get("position"):
                x, y, z = [float(v) for v in p.get("position").split(",")]
                prefabs.append({"name": p.get("name", "unknown"), "type": p.get("type", ""), "x": x, "y": y, "z": z})
    return {"name": name, "size": size, "image": dimensions, "spawns": spawns, "prefabs": prefabs,
            "registration": "Biome overlay uses a centered X/Z transform; alignment is not yet verified in-game."}


def worlds():
    base = GAME / "Data" / "Worlds"
    if not base.exists():
        return []
    return [{"name": p.name} for p in sorted(base.iterdir()) if (p / "biomes.png").exists()]


def telemetry_files():
    # Include isolated recordings first. Read the real profile only; never write to it.
    paths = list(scratch().glob("**/JonFollow/telemetry/follow-*.jsonl"))
    profile = Path(os.environ.get("APPDATA", "")) / "7DaysToDie" / "JonFollow" / "telemetry"
    paths += list(profile.glob("follow-*.jsonl"))
    unique = {str(p.resolve()): p for p in paths}
    return sorted(unique.values(), key=lambda p: p.stat().st_mtime, reverse=True)


def recording_id(path):
    return hashlib.sha256(str(path.resolve()).encode()).hexdigest()[:24]


def telemetry(path):
    events, warnings = [], []
    lines = Path(path).read_text(encoding="utf-8", errors="replace").splitlines()
    for i, line in enumerate(lines):
        if not line.strip():
            continue
        try:
            event = json.loads(line)
            if not isinstance(event, dict) or not isinstance(event.get("t"), (int, float)) or not math.isfinite(event["t"]):
                raise ValueError("missing event time")
            events.append(event)
        except (ValueError, TypeError) as error:
            warnings.append("Line " + str(i + 1) + ": " + str(error))
    events.sort(key=lambda e: e["t"])
    samples = [e for e in events if e.get("e") == "s"]
    routes = [e for e in events if e.get("e") == "route"]
    return {"name": Path(path).name, "events": events, "samples": samples, "routes": routes,
            "notes": [e for e in events if e.get("e") in ("note", "end")], "warnings": warnings,
            "start": events[0]["t"] if events else 0, "end": events[-1]["t"] if events else 0,
            "world": None, "source": "Recorded game telemetry (world not stored in this format)"}


def drawing_path(name):
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,80}", name):
        raise ValueError("Invalid drawing name")
    return workspace() / "drawings" / (name + ".json")


def validate_drawing(data):
    shapes = data.get("shapes")
    if not isinstance(shapes, list) or len(shapes) > 5000:
        raise ValueError("Expected a drawing with at most 5000 shapes")
    for shape in shapes:
        if not isinstance(shape, dict) or shape.get("type") not in ("pen", "rect", "arrow", "text"):
            raise ValueError("Unsupported drawing shape")
        points = shape.get("points")
        if not isinstance(points, list) or not 1 <= len(points) <= 20000:
            raise ValueError("Each shape needs coordinate points")
        for point in points:
            if not isinstance(point, list) or len(point) != 2 or any(
                    not isinstance(v, (int, float)) or not math.isfinite(v) for v in point):
                raise ValueError("Drawing coordinates must be finite X/Y pairs")
        if not re.fullmatch(r"#[0-9a-fA-F]{6}", shape.get("color", "#e6bd7b")):
            raise ValueError("Drawing colors must use six-digit hex")
        if not isinstance(shape.get("text", ""), str) or len(shape.get("text", "")) > 200:
            raise ValueError("Drawing labels must be at most 200 characters")
    return data


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, code, body, content_type="application/json; charset=utf-8"):
        data = body if isinstance(body, bytes) else json.dumps(body, allow_nan=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'self'; img-src 'self' blob: data:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
        self.end_headers()
        try:
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def trusted_host(self):
        return self.headers.get("Host") in (f"127.0.0.1:{self.server.server_port}", f"localhost:{self.server.server_port}")

    def do_GET(self):
        if not self.trusted_host():
            return self.reply(403, {"error": "Local hosts only"})
        parsed = urlparse(self.path)
        route, query = unquote(parsed.path), parse_qs(parsed.query)
        try:
            if route == "/":
                return self.file(STATIC / "index.html")
            if route.startswith("/static/"):
                return self.file(safe_child(STATIC, route[8:]))
            if route == "/file":
                path = safe_child(scratch(), query.get("path", [""])[0])
                if path.suffix.lower() not in (".png", ".jpg", ".jpeg", ".webp"):
                    raise ValueError("Only image artifacts may be served")
                return self.file(path)
            if route == "/api/status":
                branch = subprocess.check_output(["git", "branch", "--show-current"], cwd=ROOT, text=True).strip()
                return self.reply(200, {"branch": branch, "repo": str(ROOT), "gameAvailable": GAME.exists(),
                                        "token": TOKEN, "suites": SUITES, "previews": PREVIEW_TARGETS})
            if route == "/api/jobs":
                return self.reply(200, job_list())
            if route.startswith("/api/jobs/"):
                with LOCK:
                    job = JOBS.get(route[10:])
                    if not job:
                        raise FileNotFoundError("No such job")
                    return self.reply(200, snapshot(job))
            if route == "/api/previews":
                return self.reply(200, previews())
            if route == "/api/worlds":
                return self.reply(200, worlds())
            if route.startswith("/api/world/"):
                return self.reply(200, world(route[11:]))
            if route.startswith("/worlds/") and route.endswith("/biomes.png"):
                return self.file(safe_child(GAME / "Data" / "Worlds", route[8:-11]) / "biomes.png")
            if route == "/api/telemetry":
                return self.reply(200, [{"id": recording_id(p), "name": p.name, "modified": p.stat().st_mtime,
                                        "source": "isolated lab" if p.is_relative_to(scratch()) else "personal profile (read only)"}
                                       for p in telemetry_files()])
            if route.startswith("/api/telemetry/"):
                match = next((p for p in telemetry_files() if recording_id(p) == route[15:]), None)
                if not match:
                    raise FileNotFoundError("No such recording")
                return self.reply(200, telemetry(match))
            if route.startswith("/api/drawings/"):
                path = drawing_path(route[14:])
                return self.reply(200, json.loads(path.read_text(encoding="utf-8")) if path.exists() else {"shapes": []})
            self.reply(404, {"error": "Not found"})
        except FileNotFoundError as error:
            self.reply(404, {"error": str(error)})
        except ValueError as error:
            self.reply(400, {"error": str(error)})
        except Exception as error:
            self.reply(500, {"error": str(error)})

    def do_POST(self):
        origin = self.headers.get("Origin")
        allowed = (f"http://127.0.0.1:{self.server.server_port}", f"http://localhost:{self.server.server_port}")
        if not self.trusted_host() or (origin and origin not in allowed) or self.headers.get("X-Lab-Token") != TOKEN:
            return self.reply(403, {"error": "Use the local lab page to make changes"})
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 1000000:
                raise ValueError("Body must be between 1 byte and 1 MB")
            data = json.loads(self.rfile.read(length))
            if not isinstance(data, dict):
                raise ValueError("Expected a JSON object")
            route = unquote(urlparse(self.path).path)
            if route == "/api/jobs":
                return self.reply(202, start_job(data.get("kind"), data.get("params")))
            if route == "/api/exports":
                png = data.get("png", "")
                if not isinstance(png, str) or not png.startswith("data:image/png;base64,"):
                    raise ValueError("Expected a PNG canvas export")
                try:
                    raw = base64.b64decode(png.split(",", 1)[1], validate=True)
                    from PIL import Image
                    with Image.open(io.BytesIO(raw)) as image:
                        if image.format != "PNG" or image.width * image.height > 16000000:
                            raise ValueError("Invalid or oversized canvas export")
                        image.verify()
                except (OSError, ValueError) as error:
                    raise ValueError("Cannot read this PNG export") from error
                kind = "map" if data.get("kind") == "map" else "design"
                path = workspace() / "exports" / (kind + "-" + uuid.uuid4().hex[:12] + ".png")
                path.parent.mkdir(exist_ok=True)
                path.write_bytes(raw)
                return self.reply(201, {"saved": True, "path": str(path.relative_to(scratch())),
                                        "url": "/file?" + urlencode({"path": str(path.relative_to(scratch()))})})
            if route.startswith("/api/drawings/"):
                validate_drawing(data)
                path = drawing_path(route[14:])
                with LOCK:
                    atomic_json(path, data)
                return self.reply(200, {"saved": True, "path": str(path.relative_to(scratch()))})
            self.reply(404, {"error": "Not found"})
        except RuntimeError as error:
            self.reply(409, {"error": str(error)})
        except (ValueError, TypeError) as error:
            self.reply(400, {"error": str(error)})
        except Exception as error:
            self.reply(500, {"error": str(error)})

    def file(self, path):
        if not path.is_file():
            raise FileNotFoundError("File not found")
        content_type = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
        if path.suffix == ".js":
            content_type = "text/javascript"
        return self.reply(200, path.read_bytes(), content_type)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=7777)
    parser.add_argument("--no-open", action="store_true")
    args = parser.parse_args()
    load_jobs()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    url = f"http://127.0.0.1:{args.port}"
    print("Mod lab: " + url, flush=True)
    if not args.no_open:
        webbrowser.open(url)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("Stopped. Any running job may need inspection before restarting.")
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
