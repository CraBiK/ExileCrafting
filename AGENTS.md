# ExileCrafting

PoE1 ExileAPI overlay plugin (`net10.0-windows`, x64, ExileCore). It runs user-written C#
scripts that click currency onto an item, or drive the horticrafting station, until the item is
what the script wanted.

No automated tests. Everything is verified in game by the player, so never claim a change is
tested. UI is ExileImGui only; if a component does not exist there, ask before building a
one-off. Few big files grouped by responsibility, sections marked with `#region`. No code
comments except a one-line reminder where something is genuinely non-obvious. Commit only at the
end of a completed piece of work and propose the message first. Never add an AI co-author
trailer. KISS, YAGNI, DRY.

## Reference

- `slop/reference/scripting-api.md` - the complete `ctx` API a craft script can call.
- `slop/reference/script-authoring.md` - script shape, guard clauses, worked examples, install
  and debug.
- `slop/reference/plugin-overview.md` - runtime prerequisites, the crafter window, settings,
  repo map, build.

Read the API doc before writing or editing any `.csx`. The imports and the awaiting rule are not
guessable from the surrounding code.

## Craft scripts (.csx)

A `.csx` here is a Roslyn script compiled with `CraftContext` as its globals object, running top
to bottom with no class and no namespace. Header:

```csharp
// Name: <what it is>
// Description: <one line>
// Type: Currency
```

Hard rules:

1. Only `await` a `ctx.*` call. `Task.Delay`, `.Result`, `.Wait()` and `Task.Run` break the run
   with "called off the main thread" or "already has an operation in flight". There is no sleep.
2. Currency names are exact `BaseItemTypes.BaseName` strings, ordinal and case-sensitive
   (`"Orb of Alteration"`), taken from `ExileCrafting/data/currency.txt`. Harvest craft text
   comes from `ExileCrafting/data/harvest-crafts.txt` and must filter to exactly one row in the
   station.
3. `ApplyCurrencyUntil` / `ApplyPairUntil` conditions are synchronous. `ctx.CountMods` inside
   them; `ctx.CheckMods` and `ctx.Ask` both throw there.
4. `ctx.Item` reads memory on every access. Never cache it, re-invoke it inside lambdas.
5. Never catch `CraftGuardException` or `OperationCanceledException`. Those are the run limits
   and the abort hotkey working correctly.
6. Prefer `ApplyCurrencyUntil` over a `while` loop of `ApplyCurrency`. One pickup instead of a
   trip back to the stash per orb.
7. Only these imports exist: `System`, `System.Linq`, `System.Threading.Tasks`, `ExileCrafting`,
   `ExileCore.PoEMemory.Components`, `ExileCore.Shared.Enums`,
   `ExileCore.PoEMemory.MemoryObjects`, `ExileCore.PoEMemory.Models`, `ItemFilterLibrary`. No
   `System.Collections.Generic`.
8. `ctx.ApplyHarvest` refuses to run unless the header says `// Type: Harvest`.

Shape: read the item, choose the one action that moves this state forward, apply it, loop. Guard
on `ctx.Item.IsEmpty`, the expected `BaseName`, and `ItemRarity.Unique` first. Every action
returns `false` when it could not complete, so check it and break - the craft log already
recorded why.

Working scripts to copy from: `ExileCrafting/examples/shock-jewel.csx` (also the in-game API
reference) and `ExileCrafting/data/snippets/*.csx`.
