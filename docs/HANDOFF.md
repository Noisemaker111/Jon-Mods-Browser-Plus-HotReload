# Handoff: 7 Days to Die mod lab

For the next agent. Read this top to bottom, then `AGENTS.md`, then `tools/README.md`.
The goal is a local lab where every mod is developed, tested and *seen* without launching
the game by hand, and where the running game can be driven by code.

## 1. The mission

Jon builds 7DTD mods (a mod browser/hot-reloader plus six gameplay mods) and hates that the
only way to test is to boot the game. Build him a local factory:

- logic, UI and live-engine checks he can run and *see* from a browser;
- a top-down map to improve navigation/pathfinding;
- eventually, a code-driven "player" (JonAgent) that performs scripted tasks in the real game.

North star: change a mod, and seconds later know — without launching the game — whether it
broke, and *see* the result. Launching the game is only ever a final 1:1 confirmation.

## 2. Current state (all committed, verified)

Branch `ingame-test`. Nothing is pushed; `origin` is a public GitHub repo, so **do not push
without Jon saying so**.

| Command | What it proves | Time |
|---|---|---|
| `scripts/Test-Sim.ps1` | Real game assemblies + real config XML, all six mods' real code in-process. 43 checks. | ~5s |
| `scripts/Test-Coop.ps1` | Built DLLs vs the real game IL (Mono.Cecil), XML patch replay. 50 checks. | ~20s |
| `tools/xui-preview.py` | A real XUi patch applied to the real template/window, rendered to PNG. | <1s |
| `tools/xui-watch.py` | Save an XML, preview re-renders in ~1s. | — |
| `tools/headless.py` | Boots an isolated dedicated server (own ports 27240/27249), runs scenarios, in-engine probe, world generation, then shuts down. | min |
| `tools/engine.py` | Read-only assertions against a running server (telnet). | — |
| `tools/probe/src/Probe.cs` | An in-engine mod that runs the real category/container code inside the server. 19/0. | ~1 min |
| `scripts/test-all.ps1 [-Engine]` | One door for the fast chain (+engine tiers). | — |
| `lab/server.py` | The local web lab (tests, previews, world map). | instant |

Key facts that make it work:

- The game's `Assembly-CSharp.dll` **runs on .NET 10 with no Unity player** (for pure code),
  but `ItemClass` needs `System.Memory`, which is only present in **PowerShell 7 (pwsh)** —
  so those checks run in-process under `pwsh`, never as a bare exe.
- The game **boots headless**: `7DaysToDie.exe -batchmode -nographics -dedicated
  -configfile=... -UserDataFolder=...`, and exposes a **telnet console** (port from
  `serverconfig.xml`) with `pathTest`, `pois`, `visitpois`, `xui`, `regionreset`,
  `exportcurrentconfigs`, and `hr doctor` for the manager's own self-check.
- Harmony detours **cannot** be installed in the .NET 10 host (MonoMod limit), so Harmony is
  verified two ways: statically against real IL, and by calling the patch body directly.

## 3. Map of the repo (relevant parts)

```
sim/GameSim.cs            logic tier: real game types + real XML, runs all mod code
scripts/Test-Sim.ps1      builds mods, runs GameSim under pwsh
scripts/Test-Coop.ps1     static IL + XML patch replay
scripts/test.ps1          fast chain: archives -> compiler -> sim -> UI checks -> coop
scripts/test-all.ps1      fast chain (+ -Engine for engine tiers)
scripts/lab.py            (parallel agent) in-game driver: server + 2 clients, input, shots
scripts/follow-map.py     (parallel agent) matplotlib renderer for follow telemetry
tools/headless.py         isolated server: up/run/probe/gen/down  (my runner)
tools/engine.py           telnet assertions + declarative scenarios
tools/probe/              in-engine probe mod (SimProbe)
tools/xui-preview.py      offline XUi template/window renderer
tools/xui-watch.py        save->preview watcher
lab/server.py             the local web lab (this agent's)
lab/static/index.html      the site
docs/JonAgent.md          (parallel agent) the code-driven-player plan
docs/HANDOFF.md           this file
```

Note the naming collision: `lab/` is the **web lab**; `scripts/lab.py` is the **in-game
driver**. Consider renaming the web one to `weblab/` to avoid confusion.

## 4. Guardrails (from AGENTS.md — obey them)

- Source in `src/`; builds never install or launch the game.
- Scratch/evidence goes in the checkout home's `.scratch/` (resolve via `git rev-parse
  --git-common-dir`), never inside a worktree.
- Verify with an isolated `-UserDataFolder`, EAC disabled, separate ports, private `Mods`.
  **Never point a run at Jon's `%AppData%\7DaysToDie`**; his saves and installed mods are his.
- Never query another site's mod catalog without written permission (default is his own site).
- Land verified changes on `beta`; releases from beta are prereleases; stable needs Jon's ask.
- There is **Jon's in-progress work** in `mods/` (Follow, Pings, LootSkulls, SharedWaypoints).
  Don't commit it or overwrite it; build around it. `git status --short` shows it.
- Don't push, don't open PRs, don't message anyone.

## 5. What's next, in priority order

### A. Pathfinding diagnosis on the map (Jon's latest ask)

**Goal.** The map shows *why* a follow went wrong: the planned route, the walls the planner
saw, where it got blocked, and the start/goal — not just where the follower walked.

**Why.** Jon explicitly wants the top-down map to "help make pathfinding better." Telemetry
already logs it; the renderers ignore most of it.

**Already there.** `mods/JonFollow/src/Telemetry.cs` writes JSONL: `s` samples (player pos `p`,
leader `l`, aim `w`, `mode` walk/run/trail/blocked/arrived, `stam`, `angle`, `ahead`, `yaw`),
`route` events (`found`, `cells`, `pts[]`, `walls[]`), `note` events, and `end`. Files land in
`%AppData%/7DaysToDie/JonFollow/telemetry/follow-*.jsonl` (newest ten kept).

**Do.**
1. In `lab/server.py` + `lab/static/index.html`, extend the Map tab: draw `route.pts` (line),
   `route.walls` (dots), `note` markers, and a start marker; add toggles and a time scrubber.
   `/api/telemetry/<file>` already returns `routes`; the canvas ignores them today.
2. Add a **follow scenario** that produces telemetry on demand: in `scripts/lab.py` land
   (drive LabA to follow LabB, or use `tools/engine.py` telnet to move them), collect the
   `follow-*.jsonl`, and have the site show it.
3. Bring `scripts/follow-map.py` to parity (route + walls + notes) or fold it into the site.

**Done when.** Pick a follow run and the map shows the walked path, the planned route, the
walls, and every "stuck/blocked/no route" note on one timeline — enough to see the failure.

**Risk.** `follow-map.py` and the site must agree on the coordinate transform (see §6).

### B. JonAgent — the code-driven player

Read `docs/JonAgent.md` first; it is the authoritative design (layers: Body, Skills, Brains,
Telemetry). This is the largest item. Suggested slices:

1. **Body** (`IBody` over `EntityPlayerLocal`): only player inputs (move/turn, sprint, jump,
   crouch, attack, activate, take-all, hotbar). No teleport.
2. **`GoTo`** reusing `mods/JonFollow/src/Pathing.cs` (`GridPath`), extended for closed doors.
3. **`OpenDoor`, `Loot`, `Fight`, `ClearPoi`, `LootPoi`, `HarvestRoad`**, `Follow`.
4. **Console brain**: `agent <player> <skill> [args]`, queueable, one `skill done` log event.
5. **LLM brain** (optional, later): same catalog as tools.

It lives in a new `mods/JonAgent` (test tool, not published). Shared pathing should move to a
source both JonFollow and JonAgent include.

**Done when** (from JonAgent.md): by console command only, `agent LabA clearpoi nearest`,
`lootpoi nearest`, and `goto <x> <z>` all succeed in the lab, each leaving a telemetry map.

### C. Client-side follow, asserted

Follow runs on the local player, so it can't be proven on the headless server. Once B's Body
exists, drive LabA to follow LabB and assert the gap closes/stops at tether via the server's
`listplayers`. Until then, follow is only covered by rule-level and static checks (see §6).

### D. Grow the in-engine probe

`tools/probe/src/Probe.cs` currently: category routing (10 families) and a real `Bag`
container transfer, plus a Harmony-patch confirmation. Add: real `XUiM_LootContainer` stash
through the UI path, a real POI loot container, an entity kill, and (with a player) the
`GridPath` route-over-real-terrain check that currently skips.

### E. Breadth

- Preview the mod browser (IMGUI overlay, not XUi — needs a real client screenshot) and the
  party-portrait windows in the site.
- More engine scenarios as JSON (`tools/scenarios/`) for nav/POI/generation.
- An on-save/pre-push hook that runs the fast chain so nothing lands red.

## 6. Known gaps and gotchas

- **Follow** is not end-to-end tested (client-side). Do not claim it is.
- **Pathfinding** can't run on the headless server without a player: the server streams no
  terrain. `tools/probe` skips it honestly. It needs a client (item C).
- **Map transform** (verify before trusting): image is centred on world (0,0) and scales
  `size/image` px per world unit (Navezgane: 3072² image, 6144 map → 2 px/unit). Confirm
  against a known coordinate (spawns, or a screenshot landmark) before drawing conclusions.
- **Prefab marker parse**: `lab/server.py` regexes `<decoration ...>` attribute order; it found
  1,559 but may miss entries with a different attribute order. Verify/replace with an XML parse.
- **`Test-Coop.ps1`** compiles sources against the game API (`Assembly-CSharp`,
  `UnityEngine.CoreModule`) but **not** the game's Mono `mscorlib`/`netstandard` — referencing
  those breaks the runtime with `MissingMethodException`. Keep it that way.
- **`build.ps1`/`Build-Mod.ps1`** need Roslyn `csc.exe` at the VS BuildTools path and the
  installed game; both are hardcoded defaults — keep the override parameters.
- PowerShell 5.1 lacks `Get-FileHash`; `test.ps1` relaunches itself under `pwsh`. Use `pwsh`.
- The manager's Friend Sync restarts clients in a multi-client lab; that's why client-dependent
  engine checks skip instead of failing.
- Don't commit `__pycache__` (now ignored) and don't commit Jon's WIP.

## 7. How to verify anything

```powershell
pwsh -NoProfile -File scripts/test.ps1              # fast chain (must be green)
python tools/headless.py run                         # engine baseline scenario
python tools/headless.py probe                       # in-engine, 19/0
python tools/headless.py gen --seed TestAll          # random world, assert it
python lab/server.py                                 # http://127.0.0.1:7777
```

Screenshots and PNGs from any run land in `.scratch/`; the site lists them.

## 8. Handoff checklist

- [ ] Read `AGENTS.md`, `tools/README.md`, `docs/JonAgent.md`, this file.
- [ ] Run `scripts/test.ps1` and `tools/headless.py probe`; confirm both green.
- [ ] `python lab/server.py`, open the Map tab, confirm it renders Navezgane with spawns.
- [ ] Pick item A (map/pathfinding) and finish it, or say which item you're taking.
- [ ] Leave Jon's WIP untouched; don't push; ask before anything public.

## 9. Definition of done for the whole effort

From the browser lab, with no manual game launch, Jon can: run every mod's checks and see
pass/fail; preview any UI panel or screen; watch a pathfinding run on the map and see why it
failed; and (later) command a code-driven player to clear or loot a POI and watch the result.
