# Jon's Mod Browser + Hot Reload

Source is in `src/`. The game supplies Unity, Harmony and Newtonsoft references.
Use `powershell -NoProfile -File scripts/build.ps1` to compile and package; builds never install or launch the game.
Run `powershell -NoProfile -File scripts/test.ps1` for archive, pack and transfer invariants using production source.
`scripts/test.ps1` also runs `scripts/Test-Sim.ps1`: a headless harness that boots the installed game's real assemblies and data and runs each built mod's real code against them, no game launch. It needs PowerShell 7 (`pwsh`); `test.ps1` relaunches itself there. See `tools/README.md` for the full offline/scripted testing model: logic, static, engine and UI tiers.
Scratch, build artifacts and verification evidence belong in the checkout home's `.scratch/`.
Land verified changes on `beta`. Releases from beta are prereleases; stable promotion requires Jon's request.
Never query another site's mod catalog without written permission. The catalog defaults to Jon's `7d2dmods.gg`.
Verify with an isolated game user-data folder, EAC disabled for this code mod, and separate ports. Preserve Jon's saves and installed mods.
