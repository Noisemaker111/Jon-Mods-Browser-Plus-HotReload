# Local mod workspace

Run `python -B lab/server.py`, or double-click `lab/start.cmd`.
Open **http://127.0.0.1:7777**. Optional: `--port 7791 --no-open`.
The site is plain HTML + Tailwind CSS + browser JavaScript, with no framework or CDN.
Compiled CSS is committed, so starting the lab requires no frontend build step.
To change the styling: `cd lab`, `npm ci`, then `npm run dev` (watch) or `npm run build`.
Edit `static/index.html` for Tailwind layouts and `static/input.css` for shared controls.
Install local Python dependencies with `python -m pip install -r lab/requirements.txt`.
PowerShell 7 and the installed game are needed for checks; Node runs the small JavaScript regressions.

## Workspaces

- **Test bench**: offline logic, compatibility, full chain, and opt-in isolated engine
  checks. Live output, explicit pass/fail/skip rows and saved run history. Completed
  checks can be rerun. One job at a time avoids conflicting builds/server lifecycles.
  The custom test builder saves read-only console assertions as scenario JSON and runs
  them in a private headless server. It is not an arbitrary command/shell endpoint.
- **UI studio**: browse native game windows/templates with your mod patches applied.
  Select layers on the canvas or in the tree; drag, resize, nudge, add, duplicate, delete,
  and edit geometry, colors, text, fonts, sprite/binding names and every native attribute.
  Shared-template instances link to their inner template. Undo/redo, zoom/fit, editable
  binding JSON, player-state controls, draft recovery, XML export and native-asset PNG export.
  **Save to mod XML** adds an owned replacement patch into the selected repository mod's
  `Config/XUi_*/templates.xml` or `windows.xml`. It preserves other patches, backs up exact
  original bytes, detects stale source revisions and refuses saves masked by later mods.
  It does not write into the installed game or install a mod.
- **Asset library**: searchable, paginated actual NGUI atlas sprites with slice borders,
  native fonts, UI textures, item icons and POI preview images. Reindex extracts read-only
  from the local game. Pick a sprite or native texture to use its real XML name/path.
  Runtime textures (such as 3D player portraits) use genuine screenshot crops, explicitly
  labelled as samples. Import/crop your own screenshot without replacing runtime bindings.
  Extracted commercial game assets are private cache files, never committed or redistributed.
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

The offline studio uses actual atlas pixels and the native game font, not silhouette/icon
stand-ins. It resolves common native styles, arithmetic, conditionals, template parameters,
grids, pivots and anchors. It is **not the Unity/NGUI runtime**: controller-populated lists,
videos, live 3D renders, specialized widgets, full game expression functions and exact
font wrapping/effects may need an in-game check. Missing bindings/assets/layout targets
are listed in the canvas limitations panel. Preview binding values are samples, not
test evidence. Windows are shown in isolation; `#cam` anchors default to 1920×1080,
overridable using `viewportwidth` and `viewportheight` binding values. C#-constructed UI
is not made editable by an XML replacement patch.

The site binds only to loopback. Mutations require a local Host, same-origin request and
session token; GET requests never launch tests. Static/artifact paths are confined, and
artifact serving is limited to raster images. Nothing writes to Jon's normal game profile.
Engine checks can launch a dedicated server, but only through the isolated runner.

All generated state goes to the checkout home's `.scratch/weblab/`: jobs, previews,
scenarios, drawings, local asset cache, studio drafts/backups and exports. PNG exports
are shown in the evidence library; extracted assets are excluded. Source remains in this worktree. Stops/interrupted jobs are not
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
GET  /api/assets                     native asset metadata (no arbitrary filesystem proxy)
GET  /api/assets/file/<id>           one indexed image or font
POST /api/jobs                       {kind: "assets"} — rebuild local game asset cache
POST /api/assets/import              {name, image: "data:image/png;base64,...", crop: [x,y,w,h]}
GET  /api/studio/catalog             installed windows/templates and repository mod destinations
GET  /api/studio/scene?key=...        patched source tree + optimistic revision
POST /api/studio/render              {key, tree, values} — resolved graph, limitations, generated patch
GET  /api/studio/draft?key=...        recovered private draft or null
POST /api/studio/draft               {key, revision, owner, tree, values}
POST /api/studio/save                {key, revision, owner, tree} — source save with backup
POST /api/studio/export              {key, tree} — XML artifact + URL
POST /api/studio/png                 {key, tree, values} — real-asset PNG artifact + URL
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
python -B -m unittest discover -s lab -p test_studio.py
node --check lab/static/app.js
node --check lab/static/studio-editor.js
node lab/test-model.mjs
```

These verify rerunnable/serialized jobs, failure reporting, telemetry discovery/retention,
path confinement, local API protections, drawing persistence, custom test validation and
coordinate/replay math, native-resource rendering, bounded expressions, capture cropping,
XML save/reload, exact backups and source conflicts. The native-resource check skips
honestly if this machine has no extracted asset cache. The browser still needs interaction checks.
