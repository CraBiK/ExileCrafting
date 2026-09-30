---
applyTo: "**/*.csx"
description: ExileCrafting craft scripts - the ctx API, the awaiting rule, and the naming rules
---

# ExileCrafting craft scripts

A `.csx` here is a Roslyn script compiled with `CraftContext` as its globals object, running top
to bottom with no class and no namespace. It drives the real mouse against Path of Exile 1
through the ExileCrafting ExileAPI plugin.

Full surface: `slop/reference/scripting-api.md`. Shape and worked examples:
`slop/reference/script-authoring.md`.

## Header

```csharp
// Name: <what it is>
// Description: <one line>
// Type: Currency
```

Only the leading `//` block is parsed. `Type` is `Currency` (default) or `Harvest`, and it
decides which panel the run drives.

## Hard rules

1. Only `await` a `ctx.*` call. `Task.Delay`, `.Result`, `.Wait()` and `Task.Run` break the run
   with "called off the main thread" or "already has an operation in flight". There is no sleep;
   to wait, loop on a `ctx` call.
2. Currency names are exact `BaseItemTypes.BaseName` strings, ordinal and case-sensitive:
   `"Orb of Alteration"`, `"Deafening Essence of Greed"`. They live in
   `ExileCrafting/data/currency.txt`.
3. Harvest craft text lives in `ExileCrafting/data/harvest-crafts.txt`, must be the whole line,
   and must filter to exactly one row in the station or the call aborts.
4. `ApplyCurrencyUntil` / `ApplyPairUntil` conditions are synchronous. Use `ctx.CountMods`;
   `ctx.CheckMods` (async) and `ctx.Ask` (needs the busy executor) both throw there.
5. `ctx.Item` reads memory on every access. Never cache it across an apply, and re-invoke it
   inside lambdas: `Func<int> Total = () => ctx.Item.Mods?.ExplicitMods?.Count ?? 0;`.
6. Never catch `CraftGuardException` or `OperationCanceledException`. They are the run limits and
   the abort hotkey working correctly.
7. Prefer `ApplyCurrencyUntil` over a `while` loop of `ApplyCurrency`. One pickup instead of a
   trip back to the stash per orb.
8. Only these imports exist: `System`, `System.Linq`, `System.Threading.Tasks`, `ExileCrafting`,
   `ExileCore.PoEMemory.Components`, `ExileCore.Shared.Enums`,
   `ExileCore.PoEMemory.MemoryObjects`, `ExileCore.PoEMemory.Models`, `ItemFilterLibrary`. No
   `System.Collections.Generic`, so prefer `var` and LINQ.

## Main calls

```csharp
await ctx.ApplyCurrency("Regal Orb");
await ctx.ApplyCurrencyUntil("Orb of Alteration", () => ctx.CountMods("IncreasedLife") > 0);
await ctx.ApplyPairUntil(primary, alternate, useAlternate, done);
await ctx.AlchScourUntil(() => ctx.CountMods(refuse) == 0);
await ctx.AlchScour();
await ctx.AltAugUntil(() => ctx.CountMods(first) > 0, () => ctx.CountMods(second) > 0);
await ctx.ChaosExaltUntil(() => ctx.CountMods(first) > 0, () => ctx.CountMods(second) > 0);
await ctx.ApplyHarvest("Reforge a Rare item with random modifiers, including a Lightning modifier");
await ctx.Ask("Keep this one?");                       // never inside an *Until condition
await ctx.CheckMods(ModPool.Implicit, wanted);         // CountMods plus a reading pause
ctx.CountMods("Life", "Mana");                         // how many of the names are present
ctx.Log("..."); ctx.SetStatus("..."); ctx.PlaySound(CraftSound.Error);
ctx.Item.IsEmpty / .BaseName / .Rarity / .Mods / .Address
```

Mod names match loosely: the query is squashed to letters and digits, then searched inside the
mod's `Name`, `RawName` and `DisplayName`. Both `"IncreasedLife"` and `"Chance to Shock"` work.

## Shape

Read the item, choose the one action that moves this state forward, apply it, loop. Guard on
`ctx.Item.IsEmpty`, the expected `BaseName`, and `ItemRarity.Unique` first. Every action returns
`false` when it could not complete - check it and break, the craft log already recorded why.

```csharp
while (true)
{
    if (ctx.Item.IsEmpty) { ctx.Log("crafting slot is empty"); break; }
    if (ctx.Item.BaseName != "Large Cluster Jewel") { ctx.Log($"wrong base: {ctx.Item.BaseName}"); break; }
    if (ctx.Item.Rarity == ItemRarity.Unique) { ctx.Log("unique, nothing to do here"); break; }

    if (await ctx.CheckMods(wanted) > 0) { ctx.Log("done"); break; }
    if (!await ctx.ApplyCurrencyUntil("Orb of Alteration", () => ctx.CountMods(wanted) > 0)) break;
}
```
