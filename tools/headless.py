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


def write_config():
    text = (GAME / "serverconfig.xml").read_text()
    overrides = {
        "ServerName": "Headless", "ServerPort": str(PORT), "ServerVisibility": "0",
        "ServerAllowCrossplay": "false", "TelnetEnabled": "true", "TelnetPort": str(TELNET),
        "TelnetPassword": "", "EACEnabled": "false", "TerminalWindowEnabled": "false",
        "WebDashboardEnabled": "false", "GameWorld": "Navezgane", "GameName": "HeadlessTest",
        "ServerMaxPlayerCount": "2", "UserDataFolder": str(RUN / "server"),
        "ServerDisabledNetworkProtocols": "SteamNetworking", "EnemySpawnMode": "false",
    }
    for key, value in overrides.items():
        pattern = re.compile(r'(<property\s+name="' + key + r'"\s+value=")[^"]*(")')
        if pattern.search(text):
            text = pattern.sub(lambda m: m.group(1) + value + m.group(2), text)
        else:
            text = text.replace("</ServerSettings>", f'\t<property name="{key}" value="{value}"/>\n</ServerSettings>')
    (RUN / "serverconfig.xml").write_text(text, encoding="utf-8")


def up(wait=180):
    RUN.mkdir(parents=True, exist_ok=True)
    if state().get("pid") and alive(state()["pid"]):
        print("already running: pid " + str(state()["pid"]))
        return 0
    print("building mods...")
    mods = build_mods()
    print("mods: " + ", ".join(mods))
    write_config()
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
                    print("server ready on telnet " + str(TELNET))
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
    if ns.cmd == "run":
        code = up()
        try:
            if code == 0:
                code = _engine().run_scenario(str(TOOLS / "scenarios" / "baseline.json"))
        finally:
            down()
        return code


if __name__ == "__main__":
    sys.exit(main())
