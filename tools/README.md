# Offline + scripted testing for 7 Days to Die mods

Three tiers, each for a different class of change. Use the cheapest tier that can
actually catch the mistake; only fall through to the next when it cannot.

| Tier | What it runs | Catches | Speed |
|------|--------------|---------|-------|
| **1. Logic** (`scripts/Test-Sim.ps1`) | The mod's real code against the real game assemblies + `Data/Config` XML, in-process | Category/sort routing, item identity, protocol and ledger math, decision helpers, exceptions | ~5s |
| **2. Static** (`scripts/Test-Coop.ps1`) | The built DLLs analysed against the real game IL (Mono.Cecil) plus patch replay | Harmony targets/argument mismatches, missing native methods, XML patch collisions, package integrity | ~20s |
| **3. Engine** (`lab.py`) | Real headless dedicated server + real windowed clients, scripted input, screenshots, telnet | Navigation/follow in a live world, POIs and containers, world generation, entities, real XUi on screen, multiplayer | minutes |
| **UI** (`tools/xui-preview.py`) | The real XUi patch applied to the real template, rendered to PNG | Panel layout, spacing, colours, which controls exist before touching the game | <1s |

## UI preview (no launch)

```
python tools/xui-preview.py --template party_entry --out preview.png --size 2
python tools/xui-preview.py --template party_entry --patch mods/JonPartyPortraits/Config/XUi_InGame/templates.xml
python tools/xui-preview.py --list
python tools/xui-preview.py --template party_entry --values overrides.json
```

It finds the base template in the game's `Data/Config/XUi_*`, applies each mod's XUi
patch (`set`/`remove`/`append`/`insertBefore`/`insertAfter`), substitutes `{placeholders}`,
and draws the result with the game's box model (pos with y-down, width/height, depth,
`type="filled"` fills, `justify`, `style="iconNNpx"`, pivot). Override any placeholder
with `--values` to preview states (dead, low health, muted voice) instantly.

Symbols from the game's texture atlases are drawn as labelled placeholders. For a
pixel-exact frame, take a screenshot of a real client (`lab.py shot A out.png`) — that
is the 1:1 confirmation of what the preview laid out.

## Engine tier: live world, nav, POIs, generation, real UI

This is the existing lab. It launches an isolated headless server and two isolated
windowed clients with isolated user-data folders, injects real keyboard/mouse, captures
screenshots, and drives the server console over telnet.

```
python lab.py setup
python lab.py start server      # real headless engine, -batchmode -nographics -dedicated
python lab.py start A
python lab.py join A            # menu -> Join -> spawn, like a player
python lab.py shot A out.png    # see the real UI
python lab.py look A 40 0       # move the camera
python lab.py tel "help"        # any console command
```

On top of the lab, `tools/engine.py` turns the live engine into pass/fail checks:

```
python tools/engine.py check                 # server-up, mods, logs, poi, pathtest, navigation, ui
python tools/engine.py check navigation ui   # only named checks
python tools/engine.py assert mods JonFollow
python tools/engine.py assert log server "Follower .* arrived"
python tools/engine.py assert log server "NullReference" --forbid
python tools/engine.py config-export         # the config the engine actually applied
```

It is read-only against a running lab (never starts/stops/reconfigures it), allow-lists
known dev-launch noise, skips client-dependent checks gracefully when no client is
connected, and exits non-zero on any real failure.

The console is the automation surface and covers the engine-bound systems:

- **Navigation**: `pathTest` modes (breakblocks/climbladders/climbwalls); watch a
  follower with `listplayers` positions and `look`/screenshots.
- **POIs**: `pois`, `visitpois`, `teleportpoi`, `teleportpoirelative`.
- **Generation**: `regionreset`, `worldchunkreset`, `chunkreset`, `exportprefab`,
  `savechunkagemap`.
- **UI**: `xui open|close|reload|list`, `debugshot`, `uioptions`.
- **World state**: `exportcurrentconfigs` (the config the game actually loaded, with every
  mod patch applied), `gettime`, `listplayers`, `getgamestats`.

Add a game-side probe mod (`src/` + `Config/`) when you need to read engine values that no
console command exposes; it runs inside the real engine and can report through `Log.Out`,
a file, or the telnet console.

## Boundary

- Tiers 1–2 are deterministic and CI-friendly; tier 3 and the real client are the ground
  truth but are slower and need a GUI session for the client.
- Harmony detours cannot be installed in the .NET 10 host, so patches are validated
  statically (tier 2) and by calling their bodies directly (tier 1), not by live detour.
- Keep every engine run isolated (`-UserDataFolder=...`, private `Mods`): never point it at
  Jon's `%AppData%\7DaysToDie`. The lab already does this.
- `HotReloadTool`'s friend-sync reinstalls mods and restarts clients in a multi-client lab;
  when a client drops mid-check, client-dependent checks (navigation, ui) skip rather than
  fail, so engine runs stay meaningful without a stable client. Disable the manager's sync
  for a fully deterministic engine run.
