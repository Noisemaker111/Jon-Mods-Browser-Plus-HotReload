Jon's Co-op Quality of Life — standalone beta mod

Keep this folder offline until the game is closed. Then put the whole
JonCoopQoL folder in your active Mods folder. This is a separate mod; it is not included in the manager download. Move
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
Put one book, item mod, weapon or ammo type in its destination chest and use
the native Smart/Move matching operation to route the rest of that category.
Item mods take priority over their inherited weapon/armor tags; ammo remains
separate from weapons, and cooking pots/grills stay with tools. Native item
groups and tags classify items; chest labels are not required or parsed.
The initial contents seed the categories;
an empty chest does not become a catch-all. The Fill operation still only
fills existing exact stacks. All retains its native behavior. Item identity,
quality, modifiers, stack limits, locked slots and chest capacity remain
controlled by the game. Different books are not combined into one item.

Middle-click ground pings and automatic team waypoints
While playing with the cursor locked, aim at a place and middle-click. Three
cyan arrows pulse on the ground for six real seconds, fading during the last
two. A new ping replaces your previous ping. The host relays it to your party;
without a party it is local. Inventory, map and other modal windows do not
create pings. Aiming at a wall projects the ping down to ground at that wall.

Join the same native party to automatically share saved manual map waypoints.
Existing waypoints are requested on joining/rejoining; additions and deletions
through the normal map controls send a complete snapshot. Teammate markers
appear in the native map/list with the owner's name and can be tracked with
the normal map controls. Their unsaved copies are removed on leaving the
party, world cleanup or source reload; your original saved waypoints remain
yours. Automatic vehicle/drone waypoints are left to the native game. A
teammate deleting a shared copy locally does not delete the owner's original.

Both friends and the host need this JonCoopQoL and its matching beta
HotReloadTool. The new team channel lives in the manager bootstrap and needs
one game restart when swapping these files. Subsequent supported QoL source
edits keep the same native package identity. Older managers cannot
relay this channel. No separate service, polling timer or manager auto-update
is added. The host authenticates owners and only relays within their party.

Making the next mods quickly
The src folder is included beside the compiled DLL. The manager watches
supported C# source edits and recompiles them against the installed game;
these features share one mod's setup, lifecycle and game hooks. XML layout
changes use the existing XUI reload tools. Network mappings and some asset
or startup changes still require restarting the game.

Verification
Built against the installed game assemblies. All XML patch targets apply
to that installation. Offline checks cover all native reading definitions,
category routing including all native item modifiers/ammo/tools/weapons,
save/reload and reset persistence, respawn clock edges, team wire snapshots,
deletions, malformed packets, ownership/world checks, ping fade/pulse timing,
vehicle decisions, cancellation rules and native patch signatures. Pack
and compiler checks also pass. Native portraits, Tab/right-click, walking,
driving, ground-arrow input/rendering, native shared-map controls, loot
replication and two-player download/rejoin still need gameplay verification.
Both mods initialize in native V3.2. A native source reload replaced 27 old
patches with zero initialization failures and published the startup DLL.
Building and staging do not change the active installation.
