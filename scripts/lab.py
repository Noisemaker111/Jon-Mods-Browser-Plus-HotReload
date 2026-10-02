"""In-game test driver: launches an isolated dedicated server and two clients, sends real
keyboard/mouse input to a client window, captures screenshots and talks to the server console.

python lab.py setup                 build isolated user-data folders with the mods
python lab.py start server|A|B      launch (returns once the process is up)
python lab.py stop                  stop every lab game process
python lab.py focus A               bring client A to the front
python lab.py shot A out.png        screenshot client A's client area
python lab.py key A tab [hold_s]    tap/hold a key
python lab.py keys A "text"         type text
python lab.py click A x y [left|right|middle] [n]
python lab.py move A x y            move cursor to client coordinate
python lab.py look A dx dy          relative mouse motion (camera)
python lab.py wheel A n             scroll
python lab.py drag A x1 y1 x2 y2 [left|right] [shift]
python lab.py tel "command"         server console via telnet
python lab.py log server|A|B [pattern] [n]
"""
import ctypes, ctypes.wintypes as wt, json, os, re, shutil, socket, subprocess, sys, time
from pathlib import Path

GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die")
# Runtime state lives in the checkout home's .scratch/ingame, never in a worktree.
ROOT = Path(subprocess.check_output(["git", "-C", str(Path(__file__).resolve().parent), "rev-parse", "--path-format=absolute", "--git-common-dir"], text=True).strip()).parent / ".scratch" / "ingame"
RUN = ROOT / "run"
BUILD = ROOT / "build"
PORT, TELNET = 27140, 27149
W, H = 1280, 720
CLIENTS = {"A": {"name": "LabA", "x": 0, "y": 0}, "B": {"name": "LabB", "x": 640, "y": 300}}
JON_MODS = Path(os.environ["APPDATA"]) / "7DaysToDie" / "Mods"
THIRD_PARTY = ["BiggerStorage_v1.0.3_ianakaberlin", "QuickStack", "z30K-itemstack"]

user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))


def state():
    f = RUN / "procs.json"
    return json.loads(f.read_text()) if f.exists() else {}


def save_state(s):
    RUN.mkdir(parents=True, exist_ok=True)
    (RUN / "procs.json").write_text(json.dumps(s, indent=1))


# ---------- setup ----------
def copy_mods(dest: Path):
    if dest.exists():
        shutil.rmtree(dest)
    dest.mkdir(parents=True)
    for d in (BUILD / "gameplay").iterdir():
        for mod in d.iterdir():
            if mod.is_dir():
                shutil.copytree(mod, dest / mod.name)
    shutil.copytree(BUILD / "manager" / "HotReloadTool", dest / "HotReloadTool")
    for name in THIRD_PARTY:
        shutil.copytree(JON_MODS / name, dest / name)


def setup():
    import xml.etree.ElementTree as ET
    for who in ["server", "A", "B"]:
        copy_mods(RUN / who / "Mods")
    tree = ET.parse(GAME / "serverconfig.xml")
    root = tree.getroot()
    settings = dict(ServerName="Jon Lab", ServerPort=str(PORT), ServerVisibility="0", ServerAllowCrossplay="false",
                    TelnetEnabled="true", TelnetPort=str(TELNET), TelnetPassword="", EACEnabled="false",
                    TerminalWindowEnabled="false", WebDashboardEnabled="false", GameWorld="Navezgane",
                    GameName="LabTest", ServerMaxPlayerCount="4", UserDataFolder=str(RUN / "server"),
                    LootRespawnDays="1", DayNightLength="120", PersistentPlayerProfiles="false",
                    ServerDisabledNetworkProtocols="SteamNetworking", EnemySpawnMode="false")
    for k, v in settings.items():
        el = root.find(f"property[@name='{k}']")
        if el is None:
            el = ET.SubElement(root, "property", name=k)
        el.set("value", v)
    tree.write(RUN / "serverconfig.xml")
    print("setup done:", RUN)


# ---------- processes ----------
def start(who):
    s = state()
    exe = str(GAME / "7DaysToDie.exe")
    common = ["-noeac", "-platform=Local", "-crossplatform=None", "-serverplatforms=Local,LAN"]
    if who == "server":
        log = RUN / "server.log"
        args = [exe, "-batchmode", "-nographics", "-dedicated", f"-configfile={RUN / 'serverconfig.xml'}",
                f"-UserDataFolder={RUN / 'server'}", "-logfile", str(log)] + common
    else:
        c = CLIENTS[who]
        log = RUN / f"client{who}.log"
        args = [exe, "-skipintro", "-screen-fullscreen", "0", "-screen-width", str(W), "-screen-height", str(H),
                "-popupwindow", f"-UserDataFolder={RUN / who}", f"-PlayerName={c['name']}", "-logfile", str(log)] + common
    if log.exists():
        log.unlink()
    p = subprocess.Popen(args, cwd=str(GAME), creationflags=0x08000000 if who == "server" else 0)
    s[who] = {"pid": p.pid, "log": str(log)}
    save_state(s)
    print(who, "pid", p.pid)


def stop():
    s = state()
    for who, info in s.items():
        subprocess.run(["taskkill", "/PID", str(info["pid"]), "/T", "/F"], capture_output=True)
        print("stopped", who, info["pid"])
    save_state({})


# ---------- windows ----------
EnumWindowsProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def hwnd_of(who):
    pid = state()[who]["pid"]
    found = []

    def cb(h, _):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h):
            n = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(h, n, 256)
            if n.value == "UnityWndClass":
                found.append(h)
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    if not found:
        raise SystemExit(f"no window for {who}")
    return found[0]


def client_rect(h):
    r = wt.RECT()
    user32.GetClientRect(h, ctypes.byref(r))
    pt = wt.POINT(0, 0)
    user32.ClientToScreen(h, ctypes.byref(pt))
    return pt.x, pt.y, r.right, r.bottom


def place(who):
    h = hwnd_of(who)
    c = CLIENTS[who]
    user32.SetWindowPos(h, 0, c["x"], c["y"], 0, 0, 0x0001 | 0x0004)


def focus(who):
    h = hwnd_of(who)
    if user32.GetForegroundWindow() == h:
        return h
    # A synthetic Alt tap lets this process move the foreground window.
    key_event(0x38, True); key_event(0x38, False)
    user32.ShowWindow(h, 9)
    user32.SetForegroundWindow(h)
    time.sleep(0.25)
    if user32.GetForegroundWindow() != h:
        raise SystemExit(f"could not focus {who}")
    return h


# ---------- input ----------
class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_void_p)]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD), ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_void_p)]


class _U(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("mi", MOUSEINPUT), ("pad", ctypes.c_byte * 32)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def send(inp):
    if user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT)) != 1:
        raise OSError(ctypes.get_last_error())


SCAN = {"esc": 0x01, "1": 0x02, "2": 0x03, "3": 0x04, "4": 0x05, "5": 0x06, "6": 0x07, "7": 0x08, "8": 0x09, "9": 0x0A, "0": 0x0B,
        "-": 0x0C, "=": 0x0D, "backspace": 0x0E, "tab": 0x0F, "q": 0x10, "w": 0x11, "e": 0x12, "r": 0x13, "t": 0x14, "y": 0x15,
        "u": 0x16, "i": 0x17, "o": 0x18, "p": 0x19, "[": 0x1A, "]": 0x1B, "enter": 0x1C, "ctrl": 0x1D, "a": 0x1E, "s": 0x1F,
        "d": 0x20, "f": 0x21, "g": 0x22, "h": 0x23, "j": 0x24, "k": 0x25, "l": 0x26, ";": 0x27, "'": 0x28, "`": 0x29,
        "shift": 0x2A, "\\": 0x2B, "z": 0x2C, "x": 0x2D, "c": 0x2E, "v": 0x2F, "b": 0x30, "n": 0x31, "m": 0x32, ",": 0x33,
        ".": 0x34, "/": 0x35, "alt": 0x38, "space": 0x39, " ": 0x39, "f1": 0x3B, "f2": 0x3C, "f3": 0x3D, "f4": 0x3E,
        "f5": 0x3F, "f6": 0x40, "f7": 0x41, "f8": 0x42, "f9": 0x43, "f10": 0x44, "f11": 0x57, "f12": 0x58,
        "up": 0xE048, "down": 0xE050, "left": 0xE04B, "right": 0xE04D, "delete": 0xE053, "home": 0xE047, "end": 0xE04F}
SHIFTED = {'_': '-', '+': '=', ':': ';', '"': "'", '<': ',', '>': '.', '?': '/', '!': '1', '@': '2', '#': '3', '$': '4',
           '%': '5', '^': '6', '&': '7', '*': '8', '(': '9', ')': '0'}


def key_event(scan, down):
    flags = 0x0008 | (0 if down else 0x0002) | (0x0001 if scan > 0xFF else 0)
    i = INPUT(type=1)
    i.u.ki = KEYBDINPUT(0, scan & 0xFF, flags, 0, None)
    send(i)


def tap(name, hold=0.06):
    sc = SCAN[name.lower()]
    key_event(sc, True); time.sleep(hold); key_event(sc, False); time.sleep(0.05)


def type_text(text):
    for ch in text:
        if ch.isupper() or ch in SHIFTED:
            base = SHIFTED.get(ch, ch.lower())
            key_event(SCAN["shift"], True); tap(base, 0.03); key_event(SCAN["shift"], False)
        else:
            tap(ch, 0.03)


def mouse(flags, dx=0, dy=0, data=0):
    i = INPUT(type=0)
    i.u.mi = MOUSEINPUT(dx, dy, data & 0xFFFFFFFF, flags, 0, None)
    send(i)


BTN = {"left": (0x0002, 0x0004), "right": (0x0008, 0x0010), "middle": (0x0020, 0x0040)}


def move_to(who, x, y):
    cx, cy, _, _ = client_rect(hwnd_of(who))
    user32.SetCursorPos(cx + int(x), cy + int(y))
    time.sleep(0.05)
    mouse(0x0001, 0, 0)  # a zero relative move so the game notices the new position


def click(who, x, y, button="left", n=1):
    move_to(who, x, y)
    down, up = BTN[button]
    for _ in range(n):
        mouse(down); time.sleep(0.06); mouse(up); time.sleep(0.12)


def look(dx, dy, steps=20):
    for _ in range(steps):
        mouse(0x0001, int(dx / steps), int(dy / steps)); time.sleep(0.01)


def shot(who, out):
    from PIL import ImageGrab
    x, y, w, h = client_rect(hwnd_of(who))
    img = ImageGrab.grab(bbox=(x, y, x + w, y + h), all_screens=True)
    img.save(out)
    print(out, img.size)


def tel(cmd, wait=2.0):
    s = socket.create_connection(("127.0.0.1", TELNET), timeout=5)
    s.sendall((cmd + "\r\n").encode())
    out, end = b"", time.time() + wait
    s.settimeout(0.3)
    while time.time() < end:
        try:
            chunk = s.recv(65536)
            if not chunk:
                break
            out += chunk
        except socket.timeout:
            pass
    s.close()
    text = out.decode(errors="replace")
    banner = ("Server IP:", "Server port:", "Max players:", "Game mode:", "World:", "Game name:", "Difficulty:", "Press 'help'")
    lines = [l for l in text.splitlines() if l.strip() and not l.startswith(banner)
             and not re.match(r"^\d{4}-\d\d-\d\dT", l)]
    print("\n".join(lines[-200:]))


def log(who, pattern=None, n=40):
    p = Path(state()[who]["log"])
    lines = p.read_text(errors="replace").splitlines()
    if pattern:
        lines = [l for l in lines if re.search(pattern, l, re.I)]
    print("\n".join(lines[-int(n):]))


def wait_log(who, pattern, timeout=300):
    """One bounded wait for a log line (file-change driven via tail)."""
    log = state()[who]["log"].replace("\\", "/")
    subprocess.run([r"C:\Program Files\Git\bin\bash.exe", "-c", f"timeout {timeout} tail -n +1 -F '{log}' 2>/dev/null | grep -m1 -E '{pattern}' >/dev/null"])


def join(who):
    """Main menu -> Join a Game -> Jon Lab -> spawn, through the same clicks a player makes."""
    wait_log(who, "btnMods wired")
    time.sleep(7)
    place(who)
    focus(who); click(who, 640, 602); time.sleep(3)          # click to continue
    focus(who); click(who, 306, 467); time.sleep(1.5)        # Discord prompt OK
    focus(who); click(who, 119, 360); time.sleep(2)          # Play Game
    focus(who); click(who, 920, 26); time.sleep(4)           # Join a Game
    focus(who); click(who, 300, 229); click(who, 702, 681)   # Jon Lab -> Connect
    wait_log(who, "OpenSpawnWindow", 300)
    time.sleep(4)
    focus(who); click(who, 640, 297); time.sleep(2)          # Spawn (returning player)
    if "OpenSpawnWindow" in open(state()[who]["log"], errors="replace").read().split("Respawn almost done")[-1]:
        focus(who); click(who, 640, 484); time.sleep(1)      # first visit: spawn in random location
    print(who, "joined")


def main(a):
    cmd = a[0]
    if cmd == "join": join(a[1]); return
    if cmd == "setup": setup()
    elif cmd == "start": start(a[1])
    elif cmd == "stop": stop()
    elif cmd == "place": place(a[1])
    elif cmd == "focus": focus(a[1])
    elif cmd == "shot":
        # An unfocused game window keeps showing a stale frame; let it present fresh ones.
        was = user32.GetForegroundWindow() == hwnd_of(a[1]); focus(a[1]); time.sleep(0.15 if was else 1.2); shot(a[1], a[2])
    elif cmd == "key": focus(a[1]); tap(a[2], float(a[3]) if len(a) > 3 else 0.06)
    elif cmd == "keys": focus(a[1]); type_text(a[2])
    elif cmd == "click": focus(a[1]); click(a[1], a[2], a[3], a[4] if len(a) > 4 else "left", int(a[5]) if len(a) > 5 else 1)
    elif cmd == "move": focus(a[1]); move_to(a[1], a[2], a[3])
    elif cmd == "look": focus(a[1]); look(int(a[2]), int(a[3]))
    elif cmd == "wheel": focus(a[1]); mouse(0x0800, data=120 * int(a[2]))
    elif cmd == "drag":
        focus(a[1]); who = a[1]; btn = a[6] if len(a) > 6 else "left"; shift = len(a) > 7
        if shift: key_event(SCAN["shift"], True)
        move_to(who, a[2], a[3]); mouse(BTN[btn][0]); time.sleep(0.1)
        for t in range(1, 11):
            move_to(who, int(a[2]) + (int(a[4]) - int(a[2])) * t / 10, int(a[3]) + (int(a[5]) - int(a[3])) * t / 10); time.sleep(0.02)
        mouse(BTN[btn][1])
        if shift: key_event(SCAN["shift"], False)
    elif cmd == "tel": tel(a[1], float(a[2]) if len(a) > 2 else 2.0)
    elif cmd == "log": log(a[1], a[2] if len(a) > 2 else None, a[3] if len(a) > 3 else 40)
    else: raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
