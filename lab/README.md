# Local lab site

A small local web server for developing this lab in a browser instead of the console:
run the test tiers, look at UI previews, and see the real world map with follow-telemetry
routes on it. Local only (`127.0.0.1`), no install, nothing sent anywhere.

```
python lab/server.py            # http://127.0.0.1:7777, opens your browser
python lab/server.py --port 7788 --no-open
```

## Tabs

- **Tests** — buttons for the headless tier (~5s), the in-engine probe, the engine
  baseline, and the full chain. Output is shown inline; a job is single-flight, so a
  second click joins the running one instead of starting another.
- **Previews** — every PNG under `.scratch` (party panel, main menu, follow maps, world
  generation), click to enlarge.
- **Map** — the game's own `biomes.png` for any world, with spawn points, prefab markers
  and the newest follow telemetry route (leader in red, follower in green). Pan/zoom with
  the zoom slider; hover reads the world coordinate. This is the top-down map for
  improving pathfinding.

## API (for future tools and agents)

```
GET /api/status              repo, branch, recent commits, worlds
GET /api/previews            images under .scratch
GET /api/run?job=sim|probe|engine|chain
GET /api/worlds              worlds with map metadata
GET /worlds/<name>/biomes.png
GET /api/world/<name>        spawns (world x,y,z), prefab markers, image size, map size
GET /api/telemetry           follow telemetry files
GET /api/telemetry/<file>    parsed samples and routes
```

## Notes

- The map uses the game's own `biomes.png` (Navezgane is 3072², pregens 768²/1024²) and a
  linear transform between image pixels and world coordinates (image is centred on world
  0,0). Follow telemetry is the only route source today; the in-engine probe path could be
  logged the same way when a player is present.
- Reads only: no endpoint installs, launches the game, or writes to Jon's profile.
