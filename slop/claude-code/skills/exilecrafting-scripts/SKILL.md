---
name: exilecrafting-scripts
description: Use when writing, editing or debugging an ExileCrafting craft script (a .csx file driving the PoE1 ExileAPI crafting plugin) - rolling an item with currency, spamming alterations or essences until a mod, alch-scouring a map, repeating a harvest craft, queueing several items, or adding a snippet to the Insert Snippet menu. Also use when asked what the ctx API can do, why a script threw "off the main thread" or "already has an operation in flight", or why a mod name or currency name is not matching.
---

# ExileCrafting craft scripts

A craft script is a `.csx` C# script compiled by Roslyn with `CraftContext` as its globals
object, running top to bottom. It drives the real mouse against Path of Exile 1 through the
ExileCrafting ExileAPI plugin.

## Read before writing

- `slop/reference/scripting-api.md` - the complete `ctx` surface, types, enums, matching rules.
- `slop/reference/script-authoring.md` - script shape, guard clauses, worked examples, where the
  real currency and mod names live, how to install and debug.
- `slop/reference/plugin-overview.md` - only when the question is about the plugin itself rather
  than a script.

Read the API doc before writing any script. The imports and the awaiting rule are not guessable.

## Hard rules

1. Only `await` a `ctx.*` call. `Task.Delay`, `.Result`, `.Wait()` and `Task.Run` all break the
   run with "called off the main thread" or "already has an operation in flight".
2. Currency names are exact `BaseItemTypes.BaseName` strings, ordinal and case-sensitive:
   `"Orb of Alteration"`. Pull them from `ExileCrafting/data/currency.txt`, do not invent them.
3. `ApplyCurrencyUntil` / `ApplyPairUntil` conditions are synchronous. Use `ctx.CountMods`;
   `ctx.CheckMods` and `ctx.Ask` both throw in there.
4. `ctx.Item` reads memory on every access. Never cache it, and re-invoke it inside lambdas
   (`Func<int> Total = () => ctx.Item.Mods?.ExplicitMods?.Count ?? 0;`).
5. Never catch `CraftGuardException` or `OperationCanceledException`. They are the run limits and
   the abort hotkey working correctly.
6. Loop with `ApplyCurrencyUntil`, not `while (...) await ctx.ApplyCurrency(...)`. One pickup
   beats a round trip to the stash per orb.
7. Only these imports exist: `System`, `System.Linq`, `System.Threading.Tasks`, `ExileCrafting`,
   `ExileCore.PoEMemory.Components`, `ExileCore.Shared.Enums`,
   `ExileCore.PoEMemory.MemoryObjects`, `ExileCore.PoEMemory.Models`, `ItemFilterLibrary`. No
   `System.Collections.Generic`.
8. Harvest crafts need `// Type: Harvest` in the header, and the craft text must filter to
   exactly one row in the station. Copy the whole line from
   `ExileCrafting/data/harvest-crafts.txt`.

## Skeleton

```csharp
// Name: <what it is>
// Description: <one line>
// Type: Currency

var wanted = new[] { "IncreasedLife" };

while (true)
{
    if (ctx.Item.IsEmpty) { ctx.Log("crafting slot is empty"); break; }
    if (ctx.Item.BaseName != "<expected base>") { ctx.Log($"wrong base: {ctx.Item.BaseName}"); break; }
    if (ctx.Item.Rarity == ItemRarity.Unique) { ctx.Log("unique, nothing to do here"); break; }

    if (await ctx.CheckMods(wanted) > 0) { ctx.Log("done"); break; }

    if (!await ctx.ApplyCurrencyUntil("Orb of Alteration", () => ctx.CountMods(wanted) > 0))
    {
        ctx.Log("stopped early");
        break;
    }
}
```

Read state, choose one action, apply it, loop. Every action returns `false` when it could not
complete; check it and break, because the craft log already recorded why.

## Before finishing

- Every `await` in the file is on a `ctx.` call.
- Every currency and harvest string was taken from `ExileCrafting/data/`, not from memory.
- There is a base-name guard, so the script cannot chew on the wrong item.
- Expensive first steps are gated behind a `ctx.Ask` whose answer is remembered.
- No `try`/`catch` around the run.
- The header has `Name`, `Description` and the right `Type`.

You cannot run a script yourself. Testing is in game, by the player: Compile Check in the
editor, then Run, then Copy log.
