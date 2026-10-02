"""
Watch a mod's XUi config and re-render the offline preview on every save.

Edit `mods/<Mod>/Config/XUi_*/templates.xml`, hit save, and the PNG updates in
about a second - no game launch, no hot reload, no menu navigation. Keep the PNG
open in any image viewer.

  python tools/xui-watch.py --template party_entry --out preview.png
  python tools/xui-watch.py --template party_entry --mods mods/JonPartyPortraits

Ctrl+C to stop. Uses only polling, so there is nothing to install.
"""
import argparse
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PREVIEW = Path(__file__).resolve().parent / "xui-preview.py"


def watch_roots(mods):
    roots = [ROOT / m / "Config" for m in mods] if mods else [ROOT / "mods", ROOT / "Config"]
    files = []
    for root in roots:
        if root.exists():
            files.extend(root.rglob("*.xml"))
    return files


def stamp(files):
    return {str(f): f.stat().st_mtime for f in files if f.exists()}


def render(template, out, size, state):
    command = [sys.executable, str(PREVIEW), "--template", template, "--out", str(out), "--size", str(size)]
    if state:
        command += ["--state", state]
    result = subprocess.run(command, cwd=str(ROOT), capture_output=True, text=True)
    line = next((l for l in result.stdout.splitlines() if l.startswith("wrote")), result.stdout.strip().splitlines()[-1] if result.stdout.strip() else "")
    print(time.strftime("%H:%M:%S"), "->", line, flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--template", default="party_entry")
    parser.add_argument("--mods", nargs="*", help="mod folders to watch (default: all)")
    parser.add_argument("--out", default="preview.png")
    parser.add_argument("--size", type=float, default=2.0)
    parser.add_argument("--state", choices=None)
    args = parser.parse_args()

    files = watch_roots(args.mods)
    print("watching " + str(len(files)) + " config files; output -> " + args.out)
    render(args.template, args.out, args.size, args.state)
    seen = stamp(files)
    while True:
        time.sleep(0.7)
        current = stamp(files)
        if current != seen:
            changed = [f for f in current if seen.get(f) != current[f]]
            print(time.strftime("%H:%M:%S"), "changed:", ", ".join(Path(f).name for f in changed), flush=True)
            seen = current
            render(args.template, args.out, args.size, args.state)


if __name__ == "__main__":
    main()
