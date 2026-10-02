"""
Local lab site.

A tiny read-mostly web server for developing the 7DTD mod lab in a browser: run the
test tiers, look at UI previews, and see the real world map with follow-telemetry
routes on it. Binds to 127.0.0.1 only.

  python lab/server.py            # http://127.0.0.1:7777
  python lab/server.py --port 7788
  python lab/server.py --no-open

Endpoints (all JSON unless noted):
  GET  /                          the site
  GET  /static/<file>             assets
  GET  /api/status                repo, branch, game, tiers
  GET  /api/previews              UI preview PNGs found under .scratch
  GET  /api/run?job=sim           run the fast headless tier, return its output
  GET  /api/run?job=probe         run the in-engine probe (slow), return its output
  GET  /api/run?job=chain         run the full fast chain (slow)
  GET  /api/worlds                worlds with map metadata
  GET  /worlds/<name>/biomes.png  the game's own biome map image
  GET  /api/world/<name>          spawn points, prefab markers and transform
  GET  /api/telemetry             follow telemetry files
  GET  /api/telemetry/<file>      parsed samples and routes for one file
"""
import argparse
import glob
import json
import os
import re
import subprocess
import sys
import threading
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qs

GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die")
HERE = Path(__file__).resolve().parent
ROOT = HERE.parent                                   # worktree root
STATIC = HERE / "static"
PWSH = os.environ.get("LAB_PWSH") or "pwsh"
PORT = 7777
RUNNING = {}
LOCK = threading.Lock()


def scratch():
    try:
        common = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "--path-format=absolute", "--git-common-dir"],
                                capture_output=True, text=True, check=True).stdout.strip()
        return Path(common).parent / ".scratch"
    except Exception:
        return ROOT / ".scratch"


def run(command, timeout):
    """Run a command, capture output, never raise."""
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=timeout, cwd=str(ROOT))
        output = (result.stdout or "") + (result.stderr or "")
        return result.returncode == 0, output[-20000:]
    except subprocess.TimeoutExpired:
        return False, "timed out after " + str(timeout) + "s"
    except Exception as error:
        return False, str(error)


def run_job(job):
    """Jobs are single-flight: a second request joins the first instead of restarting."""
    with LOCK:
        existing = RUNNING.get(job)
        if existing:
            return existing
    if job == "sim":
        command, timeout = [PWSH, "-NoProfile", "-File", str(ROOT / "scripts" / "Test-Sim.ps1")], 300
    elif job == "probe":
        command, timeout = [sys.executable, str(ROOT / "tools" / "headless.py"), "probe"], 600
    elif job == "engine":
        command, timeout = [sys.executable, str(ROOT / "tools" / "headless.py"), "run"], 600
    elif job == "chain":
        command, timeout = [PWSH, "-NoProfile", "-File", str(ROOT / "scripts" / "test.ps1")], 600
    else:
        return {"ok": False, "output": "unknown job: " + str(job)}
    result = {"ok": False, "output": "running..."}
    with LOCK:
        RUNNING[job] = result
    ok, output = run(command, timeout)
    with LOCK:
        result.update(ok=ok, output=output, job=job)
    return result


def previews():
    found = []
    for path in sorted(scratch().glob("**/*.png"), key=lambda p: p.stat().st_mtime, reverse=True)[:400]:
        rel = path.resolve()
        found.append({"name": path.name, "path": str(path),
                      "url": "/file?path=" + str(rel).replace("\\", "/"),
                      "modified": int(path.stat().st_mtime)})
    return found


def safe_file(path):
    """Only serve files under the scratch home or the lab folder."""
    resolved = Path(path).resolve()
    for base in (scratch().resolve(), HERE.resolve()):
        try:
            resolved.relative_to(base)
            return resolved
        except ValueError:
            continue
    return None


def worlds():
    root = GAME / "Data" / "Worlds"
    result = []
    for folder in sorted(root.iterdir()):
        info = folder / "map_info.xml"
        image = folder / "biomes.png"
        if not image.exists():
            continue
        size = 6144
        if info.exists():
            m = re.search(r'HeightMapSize"\s+value="(\d+)\s*,\s*(\d+)"', info.read_text(errors="replace"))
            if m:
                size = int(m.group(1))
        from PIL import Image
        with Image.open(image) as img:
            width, height = img.size
        result.append({"name": folder.name, "size": size, "image": [width, height],
                       "prefabs": (folder / "prefabs.xml").exists(), "spawns": (folder / "spawnpoints.xml").exists()})
    return result


def world(name):
    folder = GAME / "Data" / "Worlds" / name
    if not folder.exists():
        return None
    size = 6144
    info = folder / "map_info.xml"
    if info.exists():
        m = re.search(r'HeightMapSize"\s+value="(\d+)\s*,\s*(\d+)"', info.read_text(errors="replace"))
        if m:
            size = int(m.group(1))
    from PIL import Image
    with Image.open(folder / "biomes.png") as img:
        width, height = img.size
    spawns = []
    sp = folder / "spawnpoints.xml"
    if sp.exists():
        for m in re.finditer(r'position="(-?\d+),(-?\d+),(-?\d+)"', sp.read_text(errors="replace")):
            spawns.append([int(m.group(1)), int(m.group(2)), int(m.group(3))])
    prefabs = []
    pf = folder / "prefabs.xml"
    if pf.exists():
        for m in re.finditer(r'<decoration[^>]*type="(\w+)"[^>]*name="([^"]+)"[^>]*position="(-?\d+),(-?\d+),(-?\d+)"', pf.read_text(errors="replace")):
            prefabs.append({"type": m.group(1), "name": m.group(2),
                            "x": int(m.group(3)), "z": int(m.group(5))})
    return {"name": name, "size": size, "image": [width, height], "spawns": spawns, "prefabs": prefabs}


def telemetry_files():
    base = Path(os.environ.get("APPDATA", "")) / "7DaysToDie" / "JonFollow" / "telemetry"
    if not base.exists():
        return []
    return sorted((p for p in base.glob("follow-*.jsonl")), key=lambda p: p.stat().st_mtime, reverse=True)


def telemetry(path):
    samples, routes = [], []
    try:
        for line in Path(path).read_text(errors="replace").splitlines():
            if not line.strip():
                continue
            event = json.loads(line)
            if event.get("e") == "s":
                samples.append({"t": event["t"], "p": event["p"], "l": event["l"], "w": event.get("w"),
                                "mode": event.get("mode"), "stam": event.get("stam"),
                                "angle": event.get("angle"), "ahead": event.get("ahead")})
            elif event.get("e") == "route":
                routes.append({"t": event["t"], "found": event["found"], "pts": event.get("pts", [])})
    except Exception as error:
        return {"error": str(error), "samples": [], "routes": []}
    return {"samples": samples, "routes": routes}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, code, body, content_type="application/json"):
        data = body if isinstance(body, bytes) else json.dumps(body).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        parsed = urlparse(self.path)
        route, query = parsed.path, parse_qs(parsed.query)
        try:
            if route in ("/", "/index.html"):
                return self.file(STATIC / "index.html", "text/html")
            if route.startswith("/static/"):
                return self.file(STATIC / route[len("/static/"):])
            if route == "/file":
                target = safe_file(query.get("path", [""])[0])
                if not target or not target.exists():
                    return self.reply(404, {"error": "not found"})
                return self.file(target)
            if route == "/api/status":
                branch = subprocess.run(["git", "-C", str(ROOT), "branch", "--show-current"],
                                        capture_output=True, text=True).stdout.strip()
                commits = subprocess.run(["git", "-C", str(ROOT), "log", "--oneline", "-5"],
                                         capture_output=True, text=True).stdout.splitlines()
                return self.reply(200, {"branch": branch, "commits": commits, "game": str(GAME),
                                        "repo": str(ROOT), "worlds": [w["name"] for w in worlds()]})
            if route == "/api/previews":
                return self.reply(200, previews())
            if route == "/api/run":
                return self.reply(200, run_job(query.get("job", ["sim"])[0]))
            if route == "/api/worlds":
                return self.reply(200, worlds())
            if route.startswith("/worlds/") and route.endswith("/biomes.png"):
                name = route[len("/worlds/"):-len("/biomes.png")]
                if not re.match(r"^[\w.-]+$", name):
                    return self.reply(400, {"error": "bad name"})
                return self.file(GAME / "Data" / "Worlds" / name / "biomes.png", "image/png")
            if route.startswith("/api/world/"):
                data = world(route[len("/api/world/"):])
                return self.reply(200 if data else 404, data or {"error": "no such world"})
            if route == "/api/telemetry":
                return self.reply(200, [{"name": p.name, "path": str(p),
                                         "modified": int(p.stat().st_mtime)} for p in telemetry_files()])
            if route.startswith("/api/telemetry/"):
                name = route[len("/api/telemetry/"):]
                match = next((p for p in telemetry_files() if p.name == name), None)
                if not match:
                    return self.reply(404, {"error": "no such telemetry file"})
                return self.reply(200, telemetry(match))
            return self.reply(404, {"error": "unknown route"})
        except Exception as error:
            return self.reply(500, {"error": str(error)})

    def file(self, path, content_type=None):
        path = Path(path)
        if not path.exists() or not path.is_file():
            return self.reply(404, {"error": "not found"})
        suffix = path.suffix.lower()
        types = {".html": "text/html", ".js": "text/javascript", ".css": "text/css",
                 ".png": "image/png", ".json": "application/json", ".svg": "image/svg+xml"}
        return self.reply(200, path.read_bytes(), content_type or types.get(suffix, "application/octet-stream"))


def main():
    global PORT
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=PORT)
    parser.add_argument("--no-open", action="store_true")
    args = parser.parse_args()
    PORT = args.port
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    url = "http://127.0.0.1:" + str(PORT)
    print("lab site: " + url)
    print("repo: " + str(ROOT))
    if not args.no_open:
        threading.Timer(0.4, lambda: webbrowser.open(url)).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("stopped")


if __name__ == "__main__":
    main()
