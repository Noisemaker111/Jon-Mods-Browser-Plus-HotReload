"""
Self-contained headless engine runner.

Brings up an isolated dedicated server (its own ports and user-data), runs the built
gameplay mods, and lets scenarios drive the real engine - navigation, POIs, world
generation, console systems - then shuts it down. It never touches the shared lab in
.scratch/ingame or Jon's real profile.

  python tools/headless.py up            build mods, start server, wait for telnet
  python tools/headless.py check         run the default engine checks against it
  python tools/headless.py scenario tools/scenarios/baseline.json
  python tools/headless.py tel "pois"
  python tools/headless.py down          stop the isolated server
  python tools/headless.py run           up -> check -> down (one command)

Ports default to 27240 (game) / 27249 (telnet), separate from the interactive lab.
"""
import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent          # worktree root
TOOLS = Path(__file__).resolve().parent
GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die")
PWsh = shutil.which("pwsh") or "powershell"
CSC = r"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
PROBE = Path(__file__).resolve().parent / "probe"
PORT, TELNET = 27240, 27249


def scratch_dir():
    """The checkout home's .scratch (per the project's AGENTS.md), not the worktree."""
    try:
        common = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "--path-format=absolute", "--git-common-dir"],
                                capture_output=True, text=True, check=True).stdout.strip()
        return Path(common).parent / ".scratch"
    except (subprocess.CalledProcessError, FileNotFoundError):
        return ROOT / ".scratch"


RUN = scratch_dir() / "headless"
STATE = RUN / "state.json"


def alive(pid):
    try:
        out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}"], capture_output=True, text=True).stdout
        return str(pid) in out
    except OSError:
        return False


def state():
    return json.loads(STATE.read_text()) if STATE.exists() else {}


def build_mods():
    """Compile the gameplay mods into the run's Mods folder."""
    builds = RUN / "builds"
    if builds.exists():
        shutil.rmtree(builds)
    folders = [p for p in (ROOT / "mods").iterdir() if p.is_dir()]
    array = ",".join("'" + str(p) + "'" for p in folders)
    command = ("& '" + str(ROOT / "scripts" / "Build-Mod.ps1") + "' -ModFolder @(" + array +
               ") -GamePath '" + str(GAME) + "' -OutputPath '" + str(builds) + "'")
    subprocess.run([PWsh, "-NoProfile", "-Command", command], check=True)
    mods = RUN / "server" / "Mods"
    if mods.exists():
        shutil.rmtree(mods)
    mods.mkdir(parents=True)
    copied = []
    for run in builds.iterdir():
        for mod in run.iterdir():
            if mod.is_dir() and (mod / "ModInfo.xml").exists():
                destination = mods / mod.name
                if not destination.exists():
                    shutil.copytree(mod, destination)
                    copied.append(mod.name)
    return sorted(copied)


def build_probe(destination):
    """Compile the in-engine probe mod into the run's Mods folder."""
    managed = GAME / "7DaysToDie_Data" / "Managed"
    package = Path(destination) / "SimProbe"
    (package / "src").mkdir(parents=True, exist_ok=True)
    references = [
        str(managed / "netstandard.dll"),
        str(managed / "System.Core.dll"), str(managed / "System.dll"),
        str(managed / "Assembly-CSharp.dll"), str(managed / "UnityEngine.CoreModule.dll"),
        str(managed / "LogLibrary.dll"),
        str(GAME / "Mods" / "0_TFP_Harmony" / "0Harmony.dll"),
    ]
    command = [CSC, "-noconfig", "-nologo", "-target:library", "-platform:x64", "-langversion:latest",
               f"-out:{package / 'SimProbe.dll'}"] + [f"-r:{r}" for r in references] + [str(PROBE / "src" / "Probe.cs")]
    subprocess.run(command, check=True, capture_output=True)
    shutil.copy(PROBE / "ModInfo.xml", package / "ModInfo.xml")
    return package


def build_manager(destination):
    """Build the manager (Jon's Mod Browser + Hot Reload) into the run and return its folder."""
    out = RUN / "manager"
    if out.exists():
        shutil.rmtree(out)
    subprocess.run([PWsh, "-NoProfile", "-File", str(ROOT / "scripts" / "build.ps1"),
                    "-GamePath", str(GAME), "-OutputPath", str(out)], check=True, capture_output=True)
    source = out / "HotReloadTool"
    target = Path(destination) / "HotReloadTool"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    return target


def write_config(extra=None):
    text = (GAME / "serverconfig.xml").read_text()
    overrides = {
        "ServerName": "Headless", "ServerPort": str(PORT), "ServerVisibility": "0",
        "ServerAllowCrossplay": "false", "TelnetEnabled": "true", "TelnetPort": str(TELNET),
        "TelnetPassword": "", "EACEnabled": "false", "TerminalWindowEnabled": "false",
        "WebDashboardEnabled": "false", "GameWorld": "Navezgane", "GameName": "HeadlessTest",
        "ServerMaxPlayerCount": "2", "UserDataFolder": str(RUN / "server"),
        "ServerDisabledNetworkProtocols": "SteamNetworking", "EnemySpawnMode": "false",
    }
    if extra:
        overrides.update(extra)
    for key, value in overrides.items():
        pattern = re.compile(r'(<property\s+name="' + key + r'"\s+value=")[^"]*(")')
        if pattern.search(text):
            text = pattern.sub(lambda m: m.group(1) + value + m.group(2), text)
        else:
            text = text.replace("</ServerSettings>", f'\t<property name="{key}" value="{value}"/>\n</ServerSettings>')
    (RUN / "serverconfig.xml").write_text(text, encoding="utf-8")


def up(wait=180, extra_config=None):
    RUN.mkdir(parents=True, exist_ok=True)
    if state().get("pid") and alive(state()["pid"]):
        print("already running: pid " + str(state()["pid"]))
        return 0
    print("building mods...")
    mods = build_mods()
    print("mods: " + ", ".join(mods))
    build_probe(RUN / "server" / "Mods")
    print("probe: SimProbe")
    build_manager(RUN / "server" / "Mods")
    print("manager: HotReloadTool")
    write_config(extra_config)
    (RUN / "server").mkdir(parents=True, exist_ok=True)
    log = RUN / "server.log"
    if log.exists():
        log.unlink()
    arguments = [str(GAME / "7DaysToDie.exe"), "-batchmode", "-nographics", "-dedicated",
                 f"-configfile={RUN / 'serverconfig.xml'}", f"-UserDataFolder={RUN / 'server'}",
                 "-logfile", str(log), "-noeac", "-platform=Local", "-crossplatform=None",
                 "-serverplatforms=Local,LAN"]
    process = subprocess.Popen(arguments, cwd=str(GAME), creationflags=0x08000000)
    STATE.write_text(json.dumps({"pid": process.pid, "log": str(log)}))
    print("server pid " + str(process.pid) + "; waiting for telnet...")
    deadline = time.time() + wait
    while time.time() < deadline:
        if not alive(process.pid):
            print("server exited early; see " + str(log))
            return 1
        try:
            with socket.create_connection(("127.0.0.1", TELNET), timeout=1) as s:
                s.sendall(b"version\r\n")
                if b"7DTD server" in s.recv(4096):
                    print("telnet up; waiting for world load...")
                    ready, _ = wait_for_log(log, r"StartGame done", wait)
                    print("server ready" if ready else "world did not report StartGame done")
                    return 0
        except OSError:
            pass
        time.sleep(2)
    print("timed out waiting for server")
    return 1


def down():
    current = state()
    if current.get("pid"):
        subprocess.run(["taskkill", "/PID", str(current["pid"]), "/T", "/F"], capture_output=True)
        print("stopped pid " + str(current["pid"]))
    STATE.write_text("{}")
    return 0


def wait_for_log(path, pattern, timeout):
    deadline = time.time() + timeout
    text = ""
    while time.time() < deadline:
        if Path(path).exists():
            text = Path(path).read_text(errors="replace")
            if re.search(pattern, text, re.I):
                return True, text
        time.sleep(2)
    return False, text


def assert_probe(timeout=240):
    """Wait for the in-engine probe to report and turn it into a check."""
    log = state().get("log", "")
    found, text = wait_for_log(log, r"\[SimProbe\] RESULT pass=\d+ fail=\d+", timeout)
    if not found:
        misses = re.findall(r"\[SimProbe\] (MISS|MISMATCH|EXC|UNPATCHED|FATAL)[^\n]*", text)
        detail = ("; " + "; ".join(misses[:5])) if misses else ""
        return False, "in-engine probe did not report" + detail
    match = re.search(r"\[SimProbe\] RESULT pass=(\d+) fail=(\d+)", text)
    passes, fails = int(match.group(1)), int(match.group(2))
    if fails:
        problems = ", ".join(re.findall(r"\[SimProbe\] (?:MISMATCH|MISS|EXC|UNPATCHED)[^\n]*", text)[:5])
        return False, f"in-engine probe {passes} pass / {fails} fail :: {problems}"
    return True, f"in-engine probe {passes} pass / 0 fail (real mod code inside the engine)"


def _engine():
    sys.path.insert(0, str(TOOLS))
    import engine
    engine.TELNET = TELNET
    engine.RUN = RUN
    return engine


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="cmd", required=True)
    sub.add_parser("up").add_argument("--wait", type=int, default=180)
    sub.add_parser("down")
    sub.add_parser("run")
    sub.add_parser("probe")
    sub.add_parser("gen").add_argument("--seed", default="HeadlessGen")
    sub.add_parser("check").add_argument("names", nargs="*")
    sub.add_parser("scenario").add_argument("file")
    sub.add_parser("tel").add_argument("command")
    ns = parser.parse_args()

    if ns.cmd == "up":
        return up(ns.wait)
    if ns.cmd == "down":
        return down()
    if ns.cmd == "tel":
        print("\n".join(_engine().tel(ns.command)))
        return 0
    if ns.cmd == "scenario":
        return _engine().run_scenario(ns.file)
    if ns.cmd == "check":
        # reuse engine's default checks against this isolated server
        engine = _engine()
        failed = 0
        for name, fn in engine.default_checks():
            if ns.names and name not in ns.names:
                continue
            ok, msg = fn()
            print(("PASS " if ok else "FAIL ") + name + ": " + msg)
            failed += 0 if ok else 1
        return 1 if failed else 0
    if ns.cmd == "probe":
        # up -> in-engine assertions -> down. Proves the real mod code inside the engine.
        code = up()
        try:
            if code == 0:
                ok, msg = assert_probe()
                print(("PASS " if ok else "FAIL ") + "probe: " + msg)
                code = 0 if ok else 1
        finally:
            down()
        return code
    if ns.cmd == "run":
        code = up()
        try:
            if code == 0:
                code = _engine().run_scenario(str(TOOLS / "scenarios" / "baseline.json"))
        finally:
            down()
        return code
    if ns.cmd == "gen":
        # Generate a random world (RWG) from a seed inside the isolated run, then confirm
        # the engine produced it and the world is live. Slower (minutes); opt-in.
        RUN.mkdir(parents=True, exist_ok=True)
        code = up(wait=180, extra_config={
            "GameWorld": "RWG", "WorldGenSeed": ns.seed, "WorldGenSize": "6144", "GameName": "HeadlessRWG",
        })
        try:
            if code != 0:
                return code
            log = state().get("log", "")
            print("generating world from seed " + ns.seed + " (this is the slow part)...")
            generated, text = wait_for_log(log, r"Generating\s+\w[\w ]*", 900)
            names = re.findall(r"Generating\s+([A-Za-z][\w ]*)", text)
            name = names[-1].strip() if names else "?"
            finished, text = wait_for_log(log, r"StartGame done", 900)
            print(("PASS" if generated else "FAIL") + " engine generated a random world: '" + name + "'")
            print(("PASS" if finished else "FAIL") + " generated world reached StartGame done")
            world_dir = RUN / "server" / "Saves" / name
            print(("PASS" if world_dir.exists() else "FAIL") + " generated world data written to " + str(world_dir))
            return 0 if (generated and finished and world_dir.exists()) else 1
        finally:
            down()


if __name__ == "__main__":
    sys.exit(main())
