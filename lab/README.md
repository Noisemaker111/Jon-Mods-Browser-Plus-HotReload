# Local mod workspace

Run `python -B lab/server.py`, or double-click `lab/start.cmd`.
Open **http://127.0.0.1:7777**. Optional: `--port 7791 --no-open`.
The site is plain HTML + Tailwind CSS + browser JavaScript, with no framework or CDN.
Compiled CSS is committed, so starting the lab requires no frontend build step.
To change the styling: `cd lab`, `npm ci`, then `npm run dev` (watch) or `npm run build`.
Edit `static/index.html` for Tailwind layouts and `static/input.css` for shared controls.
Python + Pillow, PowerShell 7 and the installed game
are needed for previews/checks; Node runs the small JavaScript regression checks.

## Workspaces

- **Test bench**: offline logic, compatibility, full chain, and opt-in isolated engine
  checks. Live output, explicit pass/fail/skip rows and saved run history. Completed
  checks can be rerun. One job at a time avoids conflicting builds/server lifecycles.
  The custom test builder saves read-only console assertions as scenario JSON and runs
  them in a private headless server. It is not an arbitrary command/shell endpoint.
- **UI studio**: render the party portrait or main menu from actual XML with player-state,
  name and distance controls. Auto-refresh rereads source every three seconds while
  this workspace is open. Icons are placeholders, not pixel-exact game textures.
  Search the evidence gallery and open images at full size.
- **World & paths**: drag to pan; wheel to zoom at the cursor; fit world or focus route.
  Choose a real recording from isolated lab clients or the personal profile (read only).
  Play/scrub the timeline; inspect follower/leader trails, current planned route, planner
  wall cells, goal, blocked samples and note events. See measured gap over time.
  Draw routes/areas/notes in world coordinates, save sketches and export PNGs.
  Zoom buttons and arrow-key panning are available for keyboard/agent use.
- **Design board**: freehand, boxes, arrows and labels for UI designs, explanations and
  test setups. Save/load and PNG export; unsaved drawings warn before leaving.

## Boundaries

The biome overview is not streamed block terrain. Its centered X/Z image registration
has not been confirmed in-game, and the existing follow recording format doesn't store
world identity: choose the matching world explicitly. Route/wall overlays use recorded
game coordinates. Drawings do not simulate movement or claim pathfinding correctness.

The site binds only to loopback. Mutations require a local Host, same-origin request and
session token; GET requests never launch tests. Static/artifact paths are confined, and
artifact serving is limited to raster images. Nothing writes to Jon's normal game profile.
Engine checks can launch a dedicated server, but only through the isolated runner.

All generated state goes to the checkout home's `.scratch/weblab/`: jobs, previews,
scenarios, drawings and PNG exports (also shown in the evidence library). Source remains in this worktree. Stops/interrupted jobs are not
marked passed. A self-contained engine run refuses to borrow an existing server.

## Agent API

GET `/api/status` supplies the token, suite catalog and branch. POST requires JSON and
`X-Lab-Token: <token>`.

```
GET  /api/jobs                       recent saved runs + live output/check rows
POST /api/jobs                       {kind: "sim" | "static" | "chain" | "probe" | "engine"}
GET  /api/jobs/<id>                   one run
POST /api/jobs                       {kind: "preview", params: {target: "party" | "menu",
                                      state: "healthy" | "low" | "dead" | "muted" | "far",
                                      values: {name: "LabA", distance: "128m"}}}
POST /api/jobs                       {kind: "scenario", params: {name: "Doctor", steps:
                                      [{expect_console: {tel: "hr doctor", pattern: "0 fail"}}]}}
GET  /api/previews                   raster evidence gallery
GET  /api/worlds                     available installed worlds
GET  /api/world/<name>               XML-parsed spawn/prefab coordinates and map dimensions
GET  /api/telemetry                  discovered real recordings (opaque IDs)
GET  /api/telemetry/<id>             complete events, walls, routes, notes + parse warnings
GET  /api/drawings/<name>            saved shapes; absent drawing returns an empty board
POST /api/drawings/<name>            {shapes: [{type: "rect" | "arrow" | "pen" | "text",
                                      points: [[x,y], ...], color: "#6ad3aa", text: "..."}]}
```

Drawing names: `design-board` (1200×700 logical pixels), `map-Navezgane` etc. (world X/Z).
For custom engine assertions, supported queries are `hr doctor`, `pois`, `gettime`,
`getgamestats`, `listplayers`, `version`; optional `forbid: true` asserts absence.

## Verification

`scripts/test.ps1` includes the core web-lab checks. For just this component:

```
python -B -m unittest discover -s lab -p test_server.py
node --check lab/static/app.js
node lab/test-model.mjs
```

These verify rerunnable/serialized jobs, failure reporting, telemetry discovery/retention,
path confinement, local API protections, drawing persistence, custom test validation and
coordinate/replay math. The browser itself still needs real interaction checks.
