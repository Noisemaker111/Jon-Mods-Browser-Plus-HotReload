# Local mod workspace

Run `python -B lab/server.py`, or double-click `lab/start.cmd`.
Open **http://127.0.0.1:7777**. Optional: `--port 7791 --no-open`.
The workspace shell is plain HTML + Tailwind CSS + browser JavaScript, with no CDN.
UI studio embeds the **complete OpenPencil 0.15.1 editor** (Vue + CanvasKit), not our own canvas.
Compiled CSS is committed, so starting the lab requires no frontend build step.
Build the local editor once with `python -B lab/pencil/build.py` (requires Bun and internet
for the pinned MIT source/dependencies). Thereafter the editor runs locally without a CDN.
Source, dependencies, WASM, app output and game assets stay in checkout-home `.scratch/`.
The build verifies its upstream archive checksum, preserves the MIT license and only
switches to a new editor build after compilation succeeds; it never installs/launches the game.
`scripts/test.ps1` also checks the adapter against OpenPencil's real graph when built.
For live-browser verification (no mod XML writes), `python -B lab/pencil/verify-browser.py`
uses Errand's already-approved connection to Jon's running Chrome and saves evidence
under `.scratch/weblab/verification/`; it never launches a second browser/profile.
Add `--save` to exercise the actual XML-save button and backup; verification restores
the exact original source and recovery-draft bytes afterward (no installation).
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
- **UI studio**: browse native game windows/templates with your mod patches applied,
  in OpenPencil's actual layer tree, canvas, tools, properties and undo history. Native
  text is editable text using extracted game fonts; real image fills include game tint,
  nine-slicing and filled bars. Game-only attributes, asset selection, bindings and
  shared-template links live below the editor. Private XML drafts recover after reload.
  OpenPencil's design-file export supports its full design toolset; XML/PNG buttons by
  the scene picker support only safe XUi translation (geometry, explicit text, native
  visibility, text size/alignment/color, duplicate/delete and basic text/frame/rectangles).
  Arbitrary vectors, rotation, gradients, font changes, effects, auto-layout and native
  depth reordering are rejected for mod saves rather than dropped. Use native attributes
  for grid/anchor-managed positions, depth, sprite tint and bindings. Shared-template
  preview internals are locked; open their source template to edit them. Runtime textures
  stay runtime bindings; captures are preview-only. Resizing a baked sprite stretches
  its design preview until a native rerender; XML/PNG export uses native slicing again.
  Cross-container global depth and Unity text/widget behavior still need runtime comparison:
  this is not claimed to be a pixel-perfect Unity renderer.
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
- **Design board**: a second full OpenPencil workspace for unrestricted designs and
  diagrams. Its File menu handles design documents and exports; it does not write mod
  XML. Earlier drawing JSON is preserved and linked, never deleted. Remote AI/cloud
  collaboration is not wired up in this local-only integration.

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
