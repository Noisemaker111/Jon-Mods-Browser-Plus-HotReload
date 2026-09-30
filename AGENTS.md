# Jon's Mod Browser + Hot Reload

Source is in `src/`. The game supplies Unity, Harmony and Newtonsoft references.
Use `powershell -NoProfile -File scripts/build.ps1` to compile and package; builds never install or launch the game.
Run `powershell -NoProfile -File scripts/test.ps1` for archive, pack and transfer invariants using production source.
Scratch, build artifacts and verification evidence belong in the checkout home's `.scratch/`.
Land verified changes on `beta`. Releases from beta are prereleases; stable promotion requires Jon's request.
Never query another site's mod catalog without written permission. The catalog defaults to Jon's `7d2dmods.gg`.
Verify with an isolated game user-data folder, EAC disabled for this code mod, and separate ports. Preserve Jon's saves and installed mods.
