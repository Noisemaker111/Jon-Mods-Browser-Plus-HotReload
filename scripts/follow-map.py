"""Draws a JonFollow telemetry file as a top-down map plus a turn/speed timeline.

python scripts/follow-map.py <follow-*.jsonl> [out.png]
Without a file it uses the newest one under %AppData%/7DaysToDie/JonFollow/telemetry.
"""
import glob, json, math, os, sys
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection

MODE_COLORS = {"walk": "#2f9e44", "run": "#e8590c", "trail": "#9c36b5", "blocked": "#e03131", "arrived": "#868e96"}


def load(path):
    events = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
    return events


def draw(path, out):
    ev = load(path)
    samples = [e for e in ev if e["e"] == "s"]
    routes = [e for e in ev if e["e"] == "route"]
    notes = [e for e in ev if e["e"] == "note"]
    if not samples:
        raise SystemExit("no samples in " + path)
    t0 = samples[0]["t"]
    fig = plt.figure(figsize=(16, 10), facecolor="white")
    ax = fig.add_axes([0.04, 0.30, 0.62, 0.66])
    side = fig.add_axes([0.70, 0.30, 0.28, 0.66])
    tl = fig.add_axes([0.04, 0.05, 0.94, 0.18])

    walls = {tuple(w) for r in routes for w in r.get("walls", [])}
    if walls:
        ax.scatter([w[0] + 0.5 for w in walls], [w[1] + 0.5 for w in walls], s=14, marker="s", color="#ced4da", label="wall at follower level", zorder=1)
    for i, r in enumerate(routes):
        pts = r["pts"]
        if pts:
            ax.plot([p[0] for p in pts], [p[2] for p in pts], color="#1c7ed6" if r["found"] else "#74c0fc",
                    lw=1, alpha=0.35 + 0.65 * (i == len(routes) - 1), zorder=2, label="planned route" if i == 0 else None)
    lx = [s["l"][0] for s in samples]; lz = [s["l"][2] for s in samples]
    ax.plot(lx, lz, color="#c92a2a", lw=1.5, ls="--", label="leader", zorder=3)
    segs, cols = [], []
    for a, b in zip(samples, samples[1:]):
        segs.append([(a["p"][0], a["p"][2]), (b["p"][0], b["p"][2])])
        cols.append(MODE_COLORS.get(a["mode"], "black"))
    ax.add_collection(LineCollection(segs, colors=cols, linewidths=2.5, zorder=4))
    for s in samples[::5]:
        yaw = math.radians(s["yaw"])
        ax.arrow(s["p"][0], s["p"][2], math.sin(yaw) * 1.2, math.cos(yaw) * 1.2, head_width=0.35, color="black", alpha=0.6, zorder=5, length_includes_head=True)
    for s in samples[::5]:
        ax.plot([s["p"][0], s["w"][0]], [s["p"][2], s["w"][2]], color="#fab005", lw=0.6, alpha=0.7, zorder=4)
    ax.scatter([samples[0]["p"][0]], [samples[0]["p"][2]], color="black", s=60, zorder=6, label="start")
    for m, c in MODE_COLORS.items():
        ax.plot([], [], color=c, lw=3, label="follower: " + m)
    ax.plot([], [], color="black", lw=1, label="aim (every 0.5 s)")
    ax.plot([], [], color="#fab005", lw=1, label="steering target")
    ax.set_aspect("equal"); ax.grid(alpha=0.2); ax.legend(loc="upper left", fontsize=8)
    ax.set_xlabel("x (east)"); ax.set_ylabel("z (north)")
    ax.set_title(os.path.basename(path) + f"  -  {len(routes)} routes ({sum(r['found'] for r in routes)} found), {len(notes)} notes")

    # Zoom of the twitchiest second: where the turn command flips most.
    flips = [i for i in range(1, len(samples)) if samples[i]["turn"] * samples[i - 1]["turn"] < 0 and abs(samples[i]["angle"]) > 20]
    centre = samples[flips[len(flips) // 2]] if flips else samples[len(samples) // 2]
    for spine in (side,):
        spine.add_collection(LineCollection(segs, colors=cols, linewidths=3))
        for s in samples:
            yaw = math.radians(s["yaw"])
            spine.arrow(s["p"][0], s["p"][2], math.sin(yaw) * 0.6, math.cos(yaw) * 0.6, head_width=0.15, color="black", alpha=0.5, length_includes_head=True)
        for r in routes:
            if r["pts"]:
                spine.plot([p[0] for p in r["pts"]], [p[2] for p in r["pts"]], color="#1c7ed6", lw=0.8, alpha=0.4)
        if walls:
            spine.scatter([w[0] + 0.5 for w in walls], [w[1] + 0.5 for w in walls], s=120, marker="s", color="#ced4da")
        spine.set_xlim(centre["p"][0] - 6, centre["p"][0] + 6); spine.set_ylim(centre["p"][2] - 6, centre["p"][2] + 6)
        spine.set_aspect("equal"); spine.grid(alpha=0.2)
        spine.set_title(f"zoom at t={centre['t'] - t0:.1f}s ({len(flips)} turn reversals)")

    ts = [s["t"] - t0 for s in samples]
    tl.plot(ts, [s["angle"] for s in samples], color="#1c7ed6", lw=1, label="heading error (deg)")
    tl.plot(ts, [s["turn"] * 10 for s in samples], color="#e8590c", lw=1, label="turn command x10")
    tl.plot(ts, [s["stam"] for s in samples], color="#2f9e44", lw=1, label="stamina")
    tl.plot(ts, [math.dist(s["p"], s["l"]) * 2 for s in samples], color="#c92a2a", lw=1, label="gap to leader x2 (m)")
    for n in notes:
        tl.axvline(n["t"] - t0, color="#adb5bd", lw=0.8)
    tl.set_xlabel("seconds"); tl.grid(alpha=0.2); tl.legend(loc="upper right", fontsize=8, ncol=4)
    fig.savefig(out, dpi=110)
    print(out)


if __name__ == "__main__":
    src = sys.argv[1] if len(sys.argv) > 1 else max(glob.glob(os.path.join(os.environ["APPDATA"], "7DaysToDie", "JonFollow", "telemetry", "follow-*.jsonl")), key=os.path.getmtime)
    draw(src, sys.argv[2] if len(sys.argv) > 2 else os.path.splitext(src)[0] + ".png")
