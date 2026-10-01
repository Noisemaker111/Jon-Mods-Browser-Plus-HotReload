# Headless mod simulation (logic tier)

`scripts/Test-Sim.ps1` boots the **installed game's own assemblies and XML** and runs each
built mod's **own code** against them — no game launch, no Unity scene. It exists so that
logic like item categories can be fully verified in seconds, instead of by booting the game.

See `tools/README.md` for the complete offline/scripted model (logic, static, engine, UI).
This file covers the logic tier.

## Why it is real, not a reimplementation

- `Assembly-CSharp.dll` (the actual game) is loaded from the install.
- `ItemClass`, `ItemValue`, `ItemStack`, `FastTags`, `StackSortUtil` and the rest are the
  game's real types.
- The 1,413 items + 107 item modifiers are read from the game's own `Data/Config/items.xml`
  and `item_modifiers.xml`, with `Extends` inheritance resolved exactly like the loader.
- Each mod DLL is loaded and its real `Categories.Of(ItemClass)` and real Harmony patch
  bodies are invoked against real objects. A pass means the shipped code ran green.

## What it catches that nothing else did

- Every native definition classifies without an exception (no null/identity/type crashes).
- Known items land in the intended destination; families never share a destination.
- The real `SortCategory.Prefix` returns the same category as `Categories.Of` for a real
  `ItemStack`, and keeps empty stacks last.
- The shipped assembly has no manager or combined-mod dependency.

## Running

```
pwsh -NoProfile -File scripts/Test-Sim.ps1
```

`scripts/test.ps1` runs it automatically (and relaunches itself under `pwsh` if needed).

## The boundary

- Logic that is pure or touches only Unity value types (`Vector3`, `Mathf`) runs fully.
- Logic needing a live scene (`GameManager.Instance`, entities, XUi) does not run here; use
  the engine tier (`lab.py`) or the UI previewer (`tools/xui-preview.py`).
- Harmony detours cannot be installed on the .NET 10 host, so patches are validated
  statically against the real game IL and by calling the patch body directly.

## Extending

Add checks in `sim/GameSim.cs` inside `RunMod`. It compiles against `Assembly-CSharp`, so
game types are strongly typed. Resolve definitions with `byName` / `modifiers`, run the
mod's real methods, and assert through `Check(condition, message)`.
