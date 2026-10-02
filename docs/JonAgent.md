# JonAgent: code-driven players for testing, later survivors

Jon's direction (2026-10-01): steering a character frame by frame with screenshots is slow and
expensive. A character must be driven by code that takes a task ("clear this POI", "loot that
house", "harvest the cars down this road") and carries it out with real pathing, real doors, real
weapons and real looting. The same architecture must later run NPC survivors that loot actual
buildings, optionally choosing their next task through an LLM. First target: the player's own body
(LabA/LabB in the lab), so every mod in this repo can be tested in the running game by command.

## Layers

1. **Body** (`IBody`). Drives a character only through the inputs a player has: move/turn
   (`MovementInput` + view rotation), sprint, jump, crouch, aim, primary/secondary attack with
   the held item, activate (doors, containers, vehicles), take all, hotbar slot. Stamina,
   animation, damage, tool wear and sounds stay native. `PlayerBody` wraps `EntityPlayerLocal`;
   a server-side `NpcBody` comes later and reuses every skill.
2. **Skills.** Coroutine-style routines on the main thread that end in `Done`, `Failed(reason)` or
   `Interrupted`. Each reports progress events.
   - `GoTo(pos | entity)`: `JonFollow` grid A* (`mods/JonFollow/src/Pathing.cs`), extended so
     closed doors are passable at a cost and opened on arrival; step-up jumps; replan on no progress.
   - `OpenDoor`, `Fight(target)` (close to weapon range, aim at the head, attack with the held
     item, back off and heal when low), `Loot(container)` (open, take all, close).
   - `ClearPoi(id)`, `LootPoi(id)` from POI bounds and the containers/enemies inside them (the same
     queries `JonLootSkulls` uses), `HarvestRoad(from, to)` for car wrecks with the held tool.
   - `Follow(player)` becomes a skill on the same body.
3. **Brains**, all choosing from one skill catalog (name, arguments, preconditions):
   - **Console/script**: `agent <player> <skill> [args]`, queueable, for tests. A test sends one
     command, waits once for the `skill done` log event, takes one screenshot as evidence.
   - **Rules**: a cheap utility matrix scores skills from a state summary (health, threats, bag
     space, nearby POIs). No model calls.
   - **LLM (optional)**: the same state summary as JSON and the skill catalog as tools; the model
     returns the next skill call. It never steers frames.
4. **Telemetry.** Every skill writes JSON lines (positions, aim, target, path, events, failure
   reasons) under the user data folder; a top-down map renderer like `scripts/follow-map.py`
   draws a run. Diagnose from data; screenshots are evidence.

## Where it lives

A separate mod `mods/JonAgent` (test tool, not published to mod sites), with the console command
registered on the client that owns the body. Shared pathing moves to source both JonFollow and
JonAgent include, so a fix lands in both.

## Done when

In the lab (`scripts/lab.py`: isolated dedicated server + two clients, Local platform players LabA
and LabB), by console command only:
- `agent LabA clearpoi nearest` kills every zombie in a house, opening doors on the way;
- `agent LabA lootpoi nearest` opens and empties every container in it, and Loot Skulls shows the
  building as looted with the right percentage and countdown;
- `agent LabA goto <x> <z>` crosses a walled compound through its gate;
- each run produces a telemetry map. Then the remaining mod tests continue through it: Category
  Storage, the Mods browser, Follow on foot and in vehicles, leaving the party.

## Lab facts learned the hard way

- Two players on one PC: `-platform=Local -crossplatform=None -PlayerName=<n>` on clients and server.
- Editing a mod's source in the server's Mods folder triggers Friend Sync, which disconnects clients
  and installs the host set; restart clients from a mirror of the server's `Mods` instead.
- Clicks right after switching windows are eaten by the game re-capturing the mouse; an unfocused
  window shows a stale frame. `lab.py shot` waits for a fresh frame.
- `World.Guid` differs per process and a client's `GameName` is its own menu value; use the world
  name for anything shared between machines.
- Enemy spawning is off in the lab config; sleepers in POIs still wake.
