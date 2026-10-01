# Headless mod simulation

`scripts/Test-Sim.ps1` boots the **installed game's own assemblies and XML** and runs each
built mod's **own code** against them — no game launch, no Unity scene. It exists so that
logic like item categories can be fully verified on a save, instead of by booting the game.

## Why it is real, not a reimplementation

- `Assembly-CSharp.dll` (the actual game) is loaded from the install.
- `ItemClass`, `ItemValue`, `ItemStack`, `FastTags`, `StackSortUtil` and the rest are the
  game's real types.
- The 1,413 items + 107 item modifiers are read from the game's own `Data/Config/items.xml`
  and `item_modifiers.xml`, with `Extends` inheritance resolved exactly like the loader.
- Each mod DLL is loaded and its real `Categories.Of(ItemClass)` and real Harmony `Prefix`
  bodies are invoked against real objects. A pass means the shipped code ran green.

## What it catches that nothing else did

- Every native definition classifies without an exception (no null/identity/type crashes).
- Known items land in the intended destination; families never share a destination.
- The real `SortCategory.Prefix` returns the same category as `Categories.Of` for a real
  `ItemStack`, and keeps empty stacks last.
- The shipped assembly has no manager or combined-mod dependency.

That is the "category thing" verified 100% without launching.

## Running

```
pwsh -NoProfile -File scripts/Test-Sim.ps1
```

`scripts/test.ps1` runs it automatically (and relaunches itself under `pwsh` if needed),
after the archive/compiler checks and before the packaging checks.

## The boundary

- Mod logic that is pure or that only touches Unity value types (`Vector3`, `Mathf`) runs
  fully: category routing, protocol round-trips, ledger math, decision helpers.
- Logic that needs a live scene (`GameManager.Instance`, entities, XUi, `Event.current`)
  cannot run here; those paths still need an in-game session.
- Harmony detours cannot be installed on the .NET 10 host (MonoMod limits), so patches are
  validated two ways instead: statically against the real game IL by `Test-Coop.ps1`, and by
  calling the patch body directly here.

## Extending

Add checks in `sim/GameSim.cs` inside `RunMod`. It compiles against `Assembly-CSharp`, so
game types are strongly typed. Resolve definitions with `byName` / `modifiers`, run the
mod's real methods, and assert. Report through `Check(condition, message)`; a failure makes
the harness exit non-zero and fails the build.
