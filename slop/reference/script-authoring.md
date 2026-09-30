# Writing an ExileCrafting script

How to get from "roll me a jewel" to a `.csx` that runs. Read
[scripting-api.md](scripting-api.md) for the full call surface first.

## Hard rules

1. Only `await` a `ctx.*` call. No `Task.Delay`, no `.Result`, no `.Wait()`, no `Task.Run`.
2. Currency base names are exact and case-sensitive. `"Orb of Alteration"`, not `"alteration"`.
3. `*Until` conditions are synchronous. `ctx.CountMods` inside them, never `ctx.CheckMods`,
   never `ctx.Ask`.
4. `ctx.Item` reads memory every access. Do not cache it, and re-invoke it inside lambdas.
5. Never catch `CraftGuardException` or `OperationCanceledException`. Those are the limits and
   the abort key doing their job.
6. Loop with `ApplyCurrencyUntil`, not with a `while` around `ApplyCurrency`. One pickup instead
   of a round trip to the stash per orb.
7. Only the imports listed in the API doc exist. `System.Collections.Generic` is not one of them.
8. `// Type: Harvest` in the header or `ctx.ApplyHarvest` refuses to run.

## The shape every script has

```csharp
// Name: <what it is>
// Description: <one line>
// Type: Currency

while (true)
{
    // 1. bail out on anything the script cannot handle
    if (ctx.Item.IsEmpty) { ctx.Log("crafting slot is empty"); break; }
    if (ctx.Item.BaseName != "Large Cluster Jewel") { ctx.Log($"wrong base: {ctx.Item.BaseName}"); break; }
    if (ctx.Item.Rarity == ItemRarity.Unique) { ctx.Log("unique, nothing to do here"); break; }

    // 2. is it already done
    if (await ctx.CheckMods(wanted) == 2) { ctx.Log("done"); break; }

    // 3. pick the one action that moves this state forward, apply it, loop
    if (!await ctx.ApplyCurrency("Orb of Scouring")) { ctx.Log("stopped early"); break; }
}
```

Read the state, decide one action, apply it, loop. Do not try to script a fixed sequence of
orbs; the item's rarity and mods after each apply are what drives the next one.

Every action returns `false` when it could not complete. Check it and `break` - the log already
says why, so the script does not need to guess.

## Guard clauses worth writing

- `ctx.Item.IsEmpty` - nothing in the slot
- `ctx.Item.BaseName != "<expected>"` - wrong item, this is the one that saves an accident
- `ctx.Item.Rarity == ItemRarity.Unique` - nothing safe to do
- rarity walk-up before a step that needs a specific rarity (alteration needs magic, regal needs
  magic, harvest reforge needs rare, alchemy needs white)

## Asking before an expensive step

`ctx.Ask` costs the player nothing when the answer is obvious and saves a lot when it isn't. Put
it before the first expensive application, not inside the loop, and remember the answer:

```csharp
var confirmed = false;
...
if (!confirmed)
{
    confirmed = await ctx.Ask("Rare jewel, no Chance to Shock yet.\n\nReforge until it rolls one?");
    if (!confirmed) { ctx.Log("stopped before the first reforge"); break; }
}
```

## Conditions that read the item

Helpers have to be `Func<>` so they re-read memory on each call. This is from the real
`CWS Cluster.csx`:

```csharp
Func<int> Total = () => ctx.Item.Mods?.ExplicitMods?.Count ?? 0;
Func<ModType, int> Slots = t => ctx.Item.Mods?.ExplicitMods?.Count(m => m.ModRecord?.AffixType == t) ?? 0;

Func<bool> Worth = () =>
{
    var n = ctx.CountMods(pool);
    return Total() == 1 ? n == 1 : n == 2 && ctx.CountMods(priority) >= 1;
};

await ctx.ApplyCurrencyUntil("Orb of Alteration", Worth);
```

Note the `?.` on `Mods` and on `ModRecord`. Both go null on a white item or a partial read.

## Two complete examples

### Currency: alt spam, regal, optional annul

```csharp
// Name: Alt Regal Annul
// Description: Alterations until a mod you want, Regal for a third, then annuls you approve
// Type: Currency

var wanted = new[] { "IncreasedLife" };

if (!await ctx.ApplyCurrencyUntil("Orb of Alteration", () => ctx.CountMods(wanted) > 0))
{
    ctx.Log("never rolled it");
    ctx.PlaySound(CraftSound.Error);
}
else if (!await ctx.ApplyCurrency("Regal Orb"))
{
    ctx.Log("no Regal Orb in the open stash tab or your inventory");
    ctx.PlaySound(CraftSound.Error);
}
else
{
    // an annul can eat the mod you kept, so ask each time and stop the moment it does
    while (await ctx.Ask("Annul a mod off this?"))
    {
        if (!await ctx.ApplyCurrency("Orb of Annulment")) break;

        if (await ctx.CheckMods(wanted) == 0)
        {
            ctx.Log("the annul took the mod you wanted");
            ctx.PlaySound(CraftSound.Error);
            break;
        }
    }
}
```

### Harvest: reforge until a mod

```csharp
// Name: Harvest Lightning Reforge
// Description: Reforge with lightning until the item rolls Chance to Shock
// Type: Harvest

var craft = "Reforge a Rare item with random modifiers, including a Lightning modifier";
var wanted = new[] { "Chance to Shock" };

while (await ctx.CheckMods(wanted) == 0)
{
    if (!await ctx.ApplyHarvest(craft))
    {
        ctx.Log("the craft didn't land - out of lifeforce, or it isn't in the station");
        ctx.PlaySound(CraftSound.Error);
        break;
    }
}
```

## Where to find real names

Do not invent strings. Three lists back the editor's Insert buttons and they are all readable
from the repo:

| What | Where | Notes |
|---|---|---|
| currency base names | `ExileCrafting/data/currency.txt` | `##` lines are groups, `#` is a comment. Validated against the game's own currency file at load, a stale line just disappears from the picker |
| harvest craft text | `ExileCrafting/data/harvest-crafts.txt` | never checked against the game, it is only the text pasted into the station's search box |
| mod ids | the game's `Mods` dat, no file | the picker inserts `ModRecord.Key` with trailing tier digits trimmed. Display names match too, see the matching section of the API doc |

More working scripts live in `ExileCrafting/data/snippets/*.csx` and
`ExileCrafting/examples/shock-jewel.csx`. The example file doubles as the in-game API reference
and is worth reading in full before writing anything new.

## Installing a script

Scripts are `.csx` files in the plugin's config folder, under `Scripts`. In game: open the
crafter (default NumPad7) and press **Open Folder** to land in exactly the right place, drop the
file in, then **Add Script** to put it on the list.

`New Script` opens the built-in editor instead, which has Compile Check, Run, and the Insert
Mod / Insert Currency / Insert Harvest Craft / Insert Snippet pickers. Editing a script on disk
is picked up automatically - the compile cache keys on the file's last write time.

## Adding a snippet

Snippets are the Insert Snippet menu. One `.csx` per snippet in `ExileCrafting/data/snippets`,
sorted by file name, hence the numeric prefixes.

```
// Name: Scour if it isn't white
// Description: Strips an item back to Normal before anything else touches it

<blank line>
<body, inserted verbatim at the caret>
```

The leading `//` run is the header, and it stops at the first blank line, so a comment that
belongs to the code stays with the code. A snippet with an empty body is skipped.

## Debugging a script that misbehaves

1. **Compile Check** in the editor. Diagnostics only, safe to run on unsaved text.
2. Read the craft log in the crafter window, then **Copy log**. Every abort logs its reason,
   including the base names it saw when a currency search failed and every craft a harvest
   filter matched when it was ambiguous.
3. A mod that visibly rolled but does not match: log `ExplicitMods` (snippet above) and shorten
   the query string.
4. "already has an operation in flight" - something async got forced with `.Result`, or `ctx.Ask`
   was called from inside a `*Until` condition.
5. "called off the main thread" - the script awaited something that is not a `ctx` call.
6. An amber stop message is a limit, not a bug. Raise Max uses, Max cost, or Max Run Seconds.
