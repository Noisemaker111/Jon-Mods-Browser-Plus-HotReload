Jon's Co-op Quality of Life — development build

Keep this folder offline until the game is closed. Then put the whole
JonCoopQoL folder next to HotReloadTool in your active Mods folder. Move
old versions into a sibling Mods-offline folder outside every active Mods
location. Keep one copy of each mod across the user and game Mods folders.
Both players need the same game build and EAC disabled for these code mods.
The game supplies 0_TFP_Harmony. No game libraries are included.

Party portraits and Follow
Join a party to see character portraits, health and stamina on the left.
Portraits use the native character preview and refresh on equipment changes.
Open inventory with Tab, right-click a friend's party row, choose Follow,
then close inventory. Walking or driving manually, jumping, using an item,
death, party departure or a disconnected/out-of-range friend cancels Follow.
You can also stop it from the on-screen button while inventory is open.
Follow pauses with a modal window open and at detected obstacles or drops.
Drive your own bicycle, minibike, motorcycle or 4x4 to follow a friend;
steering, acceleration and brakes use the game's physics. It does not enter
a vehicle for you. Gyrocopter flight is unsupported. It follows a trail of
the friend's positions and pauses at blocked paths; it cannot navigate a
maze, find an alternate route or guarantee safe unattended driving.

Loot skulls
Emptying a naturally spawned loot container marks its location/POI with a
skull. A skull means a container there was emptied, not that every container
in the building is clear. The label shows remaining game hours until the
native respawn interval; it follows nearby-player clock deferrals and hides
when that interval is eligible. Eligibility does not force the game to
repopulate an unloaded chunk. With respawn disabled the skull stays.
Refilled containers and quest POI resets remove their recorded markers.
History is saved inside that world's save/cache in JonCoopQoL/loot-skulls.xml.
Placed player storage is excluded. Clients observe replicated storage;
markers for unloaded places a client has never observed are not synchronized
as a separate world-wide discovery map.

Category sorting and chest routing
Use the normal sort button. Books, perk books, crafting magazines and
schematics share a category; medicine, food/drink, ammunition, tools,
weapons, armor/clothing, item mods, vehicles/fuel, resources, farming and
building items have their own categories.
Put one book in a chest and use the native Smart/Move matching operation to
route other reading items there. The initial contents seed the categories;
an empty chest does not become a catch-all. The Fill operation still only
fills existing exact stacks. All retains its native behavior. Item identity,
quality, modifiers, stack limits, locked slots and chest capacity remain
controlled by the game. Different books are not combined into one item.

Making the next mods quickly
The src folder is included beside the compiled DLL. The manager watches
supported C# source edits and recompiles them against the installed game;
these features share one mod's setup, lifecycle and game hooks. XML layout
changes use the existing XUI reload tools. Network mappings and some asset
or startup changes still require restarting the game.

Verification
Built against the installed game assemblies. All XML patch targets apply
to that installation. Offline checks cover all native reading definitions,
category routing, save/reload and reset persistence, respawn clock edges,
vehicle decisions, cancellation rules and native patch signatures. Pack
and compiler checks also pass. Native portraits, Tab/right-click, walking,
driving, loot replication and two-player download/rejoin have not been
exercised in this build because Jon is playing. No running game or active
mod folder was changed by building or staging it.
