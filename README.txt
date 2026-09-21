Jon's Mod Browser + Hot Reload - v6.2.0
========================================
by Noisemakerjon

WHAT YOU GET
  * MODS button on the main menu AND in the ESC menu
  * Browse/search 3,800+ mods from 7daystodiemods.com inside the game,
    filtered to YOUR game version (no more version mismatches)
  * Real card pictures, categories, search, sorting
  * One-click install / update / remove - mods hot-load instantly,
    NO game restart, ever
  * Pack codes: copy ONE short code to share a whole modlist -
    friends paste it and everything installs itself
  * Server owners: apply a pack once in the console and every player
    who joins gets a one-click "Install pack" prompt

INSTALL (2 minutes)
  1. Unzip this file.
  2. You now have a folder called "HotReloadTool".
  3. Drop that whole folder into your Mods folder. Either one works:
        - EASIEST:  %AppData%\7DaysToDie\Mods
          (paste %AppData%\7DaysToDie\Mods into Explorer's address bar,
           create the "Mods" folder if it does not exist)
        - or:  <your game folder>\Mods
          (e.g. C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die\Mods)
     So the final path looks like:
        ...\Mods\HotReloadTool\ModInfo.xml      <- ModInfo.xml must be right there
  4. Launch the game. Click MODS on the main menu. Done.

  Requires: 7 Days to Die V1.0+ / V3.x (it version-checks itself),
  single-player or non-dedicated host. Nothing else to install.

FIRST 60 SECONDS
  * MODS button (main menu or ESC menu) opens the Mods window.
  * Left = Downloaded (everything in your Mods folder, with reload/remove).
  * Right = Browse (search box top-right, categories as chips, download
    button on every card). Installs finish in seconds and hot-load.
  * F1 console:  hr status   hr doctor   hr browser open
                 hr pack make <name>     (makes a pack code from what you have)
                 hr pack apply <code>    (installs a pack; server owners use this)
  * ESC closes the window. The X on a Downloaded card removes that mod.

SERVER OWNERS
  1. Install this mod on the server (same Mods folder).
  2. In the server console:  hr pack apply <pack code>
  3. Every player who joins with this mod installed gets a one-click
     "Install pack" prompt at the top of the Mods window. That's it.

TROUBLESHOOTING
  * Window won't open: run  hr doctor  in the F1 console and read the log at
    %AppData%\7DaysToDie\logs (look for [HotReload] lines).
  * A mod's pictures are grey: it will fill in as you scroll, or it has no
    picture on 7daystodiemods.com.
  * Removing the mod = delete the HotReloadTool folder. Nothing else is touched.

Drive safely. - Jon