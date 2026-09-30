# Jon's Mod Browser + Hot Reload

An in-game **MODS** button, mod manager and hot reload tools for 7 Days to Die on Windows. This beta adds portable packs containing the actual mod files and host-to-friend downloads over the game's connection. Built against game **V3.2**.

## Download and install

1. Download **JonModsBrowser-Plus-HotReload-beta.zip** from the [GitHub beta release](https://github.com/Noisemaker111/Jon-Mods-Browser-Plus-HotReload/releases/tag/v6.3.0-beta.3). It contains one folder: `HotReloadTool`.
2. Close the game and put that folder in `%AppData%\7DaysToDie\Mods`. When replacing an older copy, preserve its `browser` folder to keep saved packs, installed-mod records and backups.
3. Launch the game with EAC disabled. Click **MODS** in the main menu or pause menu.

**JonCoopQoL is a separate, optional gameplay mod.** Its own `JonCoopQoL.zip` contains one `JonCoopQoL` folder to put in Mods. The manager download does not include it. No installer or launcher scripts are needed for either download.

Both friends need this same release, the same game build and EAC disabled for the modded world. This is a code mod; it cannot load in an EAC-protected session.

For a manual swap, copy the included `HotReloadTool` folder into your active Mods folder after closing the game. Keep the previous folder in a sibling `Mods-offline` folder outside active Mods. Have only one active copy of the manager across the user Mods folder and the game's legacy Mods folder. The download never installs itself or checks for tool updates.

## Playing with a friend

The player hosting the world supplies the shared mod set. After authentication, the client waits before loading the world while the tool compares file checksums, including manually installed mods from both Mods locations. Matching players continue joining. Different mods are downloaded from the host through the existing game connection; no separate port or catalog account is needed. Changes to the host's mods also notify connected friends. A failed initial check cancels joining instead of loading a mismatched world.

After receiving a different pack, the client leaves the world, verifies every file and installs the host's exact set. **Restart the game and rejoin** to load DLLs, assets and configuration through normal game startup. Existing versions and client-only extra mods are moved into `HotReloadTool\browser\backups`, including mods replaced from the legacy location. The manager and the game's required Harmony mod are preserved. Mods added only on a joining friend's machine do not change the host's set.

The previous release's local `serverpack.txt` marker did not transmit anything over the network. This beta replaces that behavior with a registered game network package. Large packs take time to compress and transfer. A failed or interrupted transfer does not replace installed mods; reconnect to retry.

## Packs and the browser

Open **MODS → pack**, enter a name and choose **Export portable pack**. A `.hrpack` archive appears in the saved packs list. It includes actual versions and files, even for manually installed mods. Choose **install** on a saved archive, or paste a full `.hrpack` path into the import box. Leave the world before importing; restart afterwards. Portable import merges a pack with local mods. Automatic friend sync installs the host's exact set.

Older `HRP1-…` catalog codes still work when the catalog is available. These codes contain catalog slugs, so they do not pin file versions and cannot contain manual mods. Invalid codes, missing mods and failed imports produce visible status messages. Repeated clicks do not queue duplicate installations; a failed download no longer stalls subsequent installs.

The browser defaults to Jon's catalog at `https://7d2dmods.gg/api`. **That domain was unreachable during this release's verification.** Fresh browsing and catalog downloads remain unavailable until its service is restored. Cached results, installed-mod management, portable packs and direct host transfers are separate from that service. No other site's catalog is fetched automatically.

## Hot reload

The existing XML, XUI, localization, supported C# mod reload and core swap tools are retained. File-change events trigger debounced work rather than repeated directory checks. Some mods retain runtime state or require startup registration; use a full restart for those mods and after changing a multiplayer pack. The bootstrap containing the network package requires restart when updated.

Useful F1 console commands: `hr status`, `hr doctor`, `hr browser open`, `hr browser sync`, `hr pack export <name>`, `hr pack import <full path>` and the existing reload commands. Game saves are not modified by installation.

## Making several mods

Use one offline authoring workspace with a separate ModInfo.xml and source/Config folder per idea. XML-only tweaks use the game's existing definitions; C# mods use the same game/Harmony references and the existing source-live loader. Check the installed game API once for a shared feature and reuse it across related mods. Saving supported source changes triggers recompilation after the file event settles; some startup behavior and assets still require a restart.

The development `scripts/Build-Mod.ps1` accepts one or several source mod folders. In one batch it validates ModInfo, parses XML, checks and applies supported patch operations to an in-memory copy of the installed game's XML, compiles any C# against the actual game, then writes one ZIP and checksum per mod. It reads authoring folders and writes to an offline output workspace, without installing or launching the game. It rejects missing XPath targets and unsupported validation operations; dependency-specific and conditional XML need their own game check. The ZIP includes source for live editing and compiled code; use IModApi when a DLL must initialize without this manager. No game libraries are bundled.

The in-game compiler reports complete diagnostics, rejects compiler failures even when an older DLL exists, preserves the previous successful DLL, and drains output while the compiler runs. Edits saved during compilation remain pending for the completion pass. Successful source reloads also publish the startup DLL, so your friend can receive and run that code without installing a compiler. Local compiler caches are excluded from shared packs. The published DLL is excluded from source-change detection to avoid rebuilding in a loop.

## Verification and source

The separate [`JonCoopQoL`](mods/JonCoopQoL) adds loot skulls tied to the native respawn clock, party portraits with health/stamina, right-click Follow while inventory is open (walking and ground vehicles), middle-click pulsing ground pings that fade out after six seconds, automatic party waypoint sharing, and category sorting/Smart chest routing. One representative book, item mod, weapon or ammo type seeds its whole destination category using native groups/tags. Item mods outrank weapon/armor tags, ammunition stays separate from weapons, and individual identities, stacks, locks and capacity remain native. Shared waypoint copies use the native map/list and are excluded from the recipient's save; party/world/source cleanup removes them. Both players and the host need the matching manager and QoL mod. [Its readme](mods/JonCoopQoL/README.txt) describes controls and limits.

The release has production-source checks for pack save/reload, both Mods locations, exact file matching, protected manager identity, version mismatch, corrupt/interrupted archives, traversal, replacement rollback and queue recovery after failure. Both DLLs compile against the installed game's actual assemblies.

Both mods loaded in isolated native V3.2 games; the engine API check reported 19 passes and 0 failures. The browser rendered in the game. A native QoL source rebuild swept 27 old patches, initialized the replacement with zero failures and published the startup DLL; an event observation confirmed there was no repeated rebuild or XML reapply. **Two-human party features and the complete join/download/restart/rejoin flow require gameplay verification.** See the release notes for the observed controls and remaining limits. This remains a prerelease.

Source is in `src/`. `scripts/build.ps1` accepts the game and Roslyn compiler paths and creates a ZIP without installing or launching it. `scripts/test.ps1` runs the production archive and install-queue checks in the checkout home's `.scratch` directory. Game assemblies are references only and are not redistributed. `HotReloadTool/build.json` records the source revision, reference assembly digest and release-file checksums.

`scripts/Stage-Release.ps1` builds the manager and the optional QoL mod from committed source into separate ZIPs/checksums in a fresh offline folder. Each ZIP contains only its own mod folder. It extracts both downloads and verifies their structure and every mod file. `Stage-Coop.ps1` remains a compatibility alias for this separate-release workflow. It refuses existing output folders and active Mods destinations. Building never installs, launches or publishes.
