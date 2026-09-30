# ExileCrafting script API

Everything a `.csx` craft script can call. Source of truth: `ExileCrafting/CraftRun.cs`
(`CraftContext`), `ExileCrafting/Scripting.cs` (`ScriptHost`).

## Execution model

A script is a C# script file (`.csx`) compiled by Roslyn with `CraftContext` as the globals
object. It runs top to bottom as top-level statements. No class, no `Main`, no namespace.

`CraftContext` exposes itself as `ctx`, so both of these compile:

```csharp
ctx.Log("hello");   // preferred, this is what every example uses
Log("hello");       // also works, globals members are in scope bare
```

Always write `ctx.`. It reads clearer and matches every shipped example.

### Pre-added references and imports

Compiled against these assemblies only:

- the ExileCrafting plugin assembly
- `ExileCore`
- `ItemFilterLibrary`
- `System.Linq`

With these `using`s already applied (do not re-declare them):

```
System, System.Linq, System.Threading.Tasks, ExileCrafting,
ExileCore.PoEMemory.Components, ExileCore.Shared.Enums,
ExileCore.PoEMemory.MemoryObjects, ExileCore.PoEMemory.Models, ItemFilterLibrary
```

Roslyn always references the core runtime on top of that, so plain BCL types work. Anything from
a namespace not in the list above has to be fully qualified, and anything in an assembly not in
the list above is simply unavailable. In particular `System.Collections.Generic` is not imported,
so prefer `var` and LINQ over spelling out `List<T>` / `Dictionary<K,V>`.

### The awaiting rule

**Only ever `await` a `ctx.*` call.**

Every `ctx` action completes on the game's main thread inside `Tick`. Awaiting anything else
(`Task.Delay`, `Task.Run`, `HttpClient`, ...) resumes the script on a threadpool thread, where
ExileCore's memory reads are not safe. The next `ctx` call detects this and throws:

> A craft action was called off the main thread. This usually means the script awaited something
> other than a ctx call...

Also never use `.Result` or `.Wait()` on a `ctx` task. Forcing `ctx.CheckMods(...).Result`
deadlocks against the executor and throws `ClickExecutor already has an operation in flight`.

To pause, loop on a `ctx` call. There is no sleep.

## Header

The leading `//` block is metadata. Parsing stops at the first line that is not a comment, so a
`// Name:` further down the file is just a comment.

```csharp
// Name: Shock Jewel
// Description: Bring a Cobalt Jewel to rare, then reforge until it rolls Chance to Shock
// Type: Harvest
```

- `Name` - shown in the crafter list. Falls back to the file name.
- `Description` - shown in the list. Optional.
- `Type` - `Currency` (default) or `Harvest`. Case-insensitive, anything that is not `harvest`
  reads as `Currency`.

`Type` decides which panel the run drives and which slot `ctx.Item` reads. `ctx.ApplyHarvest`
aborts outright unless the header says `Harvest`.

## Actions

All async, all return `Task<bool>`, all `false` means the action did not complete. All of them
count against the run limits.

### `await ctx.ApplyCurrency(string baseName)`

Right-clicks the currency stack, then left-clicks the item in the craft slot. Searches the open
stash tab first, then the player inventory.

`baseName` must be the exact `BaseItemTypes.BaseName` string, matched ordinal and case-sensitive:
`"Orb of Alteration"`, `"Regal Orb"`, `"Deafening Essence of Greed"`. A typo just fails and logs
every base name it did see.

### `await ctx.ApplyCurrencyUntil(string baseName, Func<bool> done)`

One pickup, then shift-clicks the same pixel until `done()` returns true. Far faster than a loop
of `ApplyCurrency`, which walks the mouse back to the stack every single application.

- `done` is **synchronous**. Use `ctx.CountMods`, never `ctx.CheckMods` (async) and never
  `ctx.Ask` (needs the executor that this loop is holding).
- Checked once before the pickup, so an already-satisfied condition touches nothing.
- Between clicks the run waits the Action delay, which is the reading pause a person would take.
- Stops on the condition, on running out of currency, on a click that did not change the item,
  or on a limit.

```csharp
await ctx.ApplyCurrencyUntil("Orb of Alteration", () => ctx.CountMods("IncreasedLife") > 0);
```

### `await ctx.ApplyPairUntil(string primary, string alternate, Func<bool> useAlternate, Func<bool> done)`

Two currencies on one pickup. Shift holds an orb on the cursor, ALT swaps which orb it is, so
neither the mouse nor the stack is ever revisited. `useAlternate()` decides what the next click
needs; `done()` stops the run. Both sync.

The swap is confirmed on the cursor before every click, so a bad pairing stops the run instead of
clicking the wrong orb onto the item. Both names are counted and priced separately.

### `await ctx.AlchScourUntil(Func<bool> done)`

`ApplyPairUntil("Orb of Alchemy", "Orb of Scouring", () => ctx.Item.Rarity != ItemRarity.Normal, done)`.
The map roller. Needs both orbs in the tab.

```csharp
var refuse = new[] { "MapPlayerNoRegeneration", "MapMonsterReflect" };
await ctx.AlchScourUntil(() => ctx.CountMods(refuse) == 0);
```

### `await ctx.AlchScour()`

One reroll with two pickups: scours if the item is not white, then alchs once. Leaves it rare.

### `await ctx.RollThenAddUntil(string primary, string alternate, int maxMods, Func<bool> addWhen, Func<bool> done)`

Spam the primary until `addWhen()` likes the roll, alt-swap to the alternate for one more mod, then
go back to spamming when `done()` still isn't satisfied. `maxMods` counts explicits and is what
keeps the alternate off a full item. `ctx.ModCount` is the same count if a condition needs it.

### `await ctx.AltAugUntil(Func<bool> augWhen, Func<bool> done)`

`RollThenAddUntil("Orb of Alteration", "Orb of Augmentation", 2, augWhen, done)`. Needs a magic item
and both orbs in the tab.

```csharp
await ctx.AltAugUntil(
    () => ctx.CountMods("IncreasedLife") > 0,
    () => ctx.CountMods("IncreasedLife") > 0 && ctx.CountMods("IncreasedMana") > 0);
```

### `await ctx.ChaosExaltUntil(Func<bool> exaltWhen, Func<bool> done, int maxMods = 6)`

Same on a rare, with Chaos Orb and Exalted Orb. Pass `maxMods: 4` for jewels, otherwise the exalt
gets tried on a full item and the run stops on a click that changed nothing.

### `await ctx.ApplyHarvest(string craftText)`

Harvest scripts only. Focuses the horticrafting station, ctrl+F filters the craft list by
`craftText`, and presses Craft once.

- The station window must be open and the craft slot must hold an item.
- `craftText` has to filter down to **exactly one** visible craft. Zero matches or two aborts and
  logs what it matched. Paste the whole line from `ExileCrafting/data/harvest-crafts.txt`.
- The selection is remembered between calls, so repeats are one click and no search.
- Lifeforce has no price source, so harvest never counts against a chaos budget.

### `await ctx.Ask(string message, PromptButtons buttons = PromptButtons.YesNo)`

Modal question, resolves to `true` on Yes/OK. Closing with X or Escape counts as No/Cancel. The
game keeps rendering while the script is suspended, and the player's thinking time is not charged
to the run clock.

**Cannot be called from inside an `ApplyCurrencyUntil` / `ApplyPairUntil` condition.** The answer
arrives on the render thread, which cannot run while a click routine is in flight. It throws a
message saying exactly that. Ask before or after the loop.

`PromptButtons.YesNo` | `PromptButtons.OkCancel`.

## Reading the item

`ctx.Item` is a `CraftItem` read fresh from memory on every access. Never cache it across an
apply, and inside a lambda call it rather than closing over a snapshot.

| Member | Type | Notes |
|---|---|---|
| `ctx.Item.IsEmpty` | `bool` | nothing in the slot |
| `ctx.Item.BaseName` | `string` | `"Large Cluster Jewel"`, `""` when empty |
| `ctx.Item.Rarity` | `ItemRarity` | `Normal` / `Magic` / `Rare` / `Unique` |
| `ctx.Item.Mods` | `Mods` | may be null, always use `?.` |
| `ctx.Item.Address` | `long` | entity address, 0 when empty |

`ctx.Item.Mods` is ExileCore's `Mods` component. The lists are `List<ItemMod>`: `.ExplicitMods`,
`.ImplicitMods`, `.EnchantedMods`, `.FracturedMods`, `.SynthesisMods`, and `.ItemMods` for all of
them. Also `.ItemRarity`, `.ItemLevel`, `.Identified`, `.IsMirrored`, `.CountFractured`.

Each `ItemMod` has `.Name`, `.RawName`, `.DisplayName`, `.Translation`, `.Group`, `.Level`,
`.Value1` to `.Value4`, `.Values`, and `.ModRecord`, whose `.AffixType` is a `ModType.Prefix` or
`ModType.Suffix`.

## Matching mods

```csharp
int  ctx.CountMods(params string[] names)                 // ModPool.Explicit
int  ctx.CountMods(ModPool pool, params string[] names)
Task<int> ctx.CheckMods(params string[] names)            // ModPool.Explicit
Task<int> ctx.CheckMods(ModPool pool, params string[] names)
bool ctx.IsMod(ItemMod mod, string wanted)
string ctx.Squash(string s)
```

`CountMods` returns **how many of the names given are present**, not how many mods matched. Two
names, both on the item, returns 2.

`CheckMods` is `CountMods` plus a randomised reading pause (the Check mods delay). Prefer it for
"has it rolled X yet" between applies. Use the sync `CountMods` inside `*Until` conditions.

`ModPool` is `Explicit` (default) / `Implicit` / `Enchanted` / `Any`.

### How a name matches

`Squash` drops case and everything that is not a letter or digit, then the squashed target is
looked for **inside** the mod's `Name`, `RawName` and `DisplayName`.

So `"Master of Fire"`, `"MasterOfFire"` and `"master-of-fire"` are the same query, and a shorter
fragment matches more. Both of these work:

- the mod id, which is what the editor's Insert Mod button pastes: `"IncreasedLife"`,
  `"MapPlayerNoRegeneration"` (a `ModsDat` key with its tier digits trimmed, so one string covers
  every tier)
- the display text you read in game: `"Chance to Shock"`

When a script disagrees with what you can plainly see on the item, log the real text and shorten
the query:

```csharp
var mods = ctx.Item.Mods?.ExplicitMods;
ctx.Log("mods: " + (mods == null || mods.Count == 0
    ? "none"
    : string.Join(", ", mods.Select(m => string.IsNullOrEmpty(m.DisplayName) ? m.Name : m.DisplayName))));
```

## Output

| Call | Effect |
|---|---|
| `ctx.Log(string)` | line in the craft log, kept in the run summary, lifted out by Copy log |
| `ctx.SetStatus(string)` | the status line on the game panel, overwritten as you go |
| `ctx.PlaySound(CraftSound)` | plays the wav bound to that slot in the Sounds tab |

`CraftSound.Error` and `CraftSound.Notification` never fire on their own, they are yours.
`Succeeded`, `Failed` and `QueueComplete` fire from the plugin, do not call them.

## Run state

Read-only, useful for logging or stopping early.

| Member | Meaning |
|---|---|
| `ctx.Applications` / `ctx.MaxApplications` | applications used and the cap, 0 means no cap |
| `ctx.SpentChaos` / `ctx.MaxChaos` | chaos spent and the budget, needs the Ninja Price plugin |
| `ctx.Elapsed` / `ctx.MaxSeconds` | `TimeSpan` on this item excluding `Ask` time, and the cap |
| `ctx.Used` | `IReadOnlyDictionary<string,int>`, name to count |
| `ctx.UsageSummary()` | that dictionary as one readable line |
| `ctx.StatusText` | the last `ctx.Log` line |

## Limits

Max uses, max cost and max run seconds are enforced by the plugin before each application. When
one trips it throws `CraftGuardException`, which the plugin reports as a normal stop with an
amber message, not a crash.

Do not check the limits yourself and **do not catch the exception**. Catching it turns a clean
stop into a script that keeps clicking.

Cost is charged before the click, so a budget is a ceiling the run never crosses. With Ninja Price
not loaded every price reads 0 and the budget never trips.

## Cancellation

The abort hotkey cancels the token. Every `ctx` call throws `OperationCanceledException` on a
cancelled run. Do not catch it, and do not wrap a whole script in `try/catch`.

## Enums quick list

```csharp
ItemRarity.Normal | Magic | Rare | Unique          // ExileCore.Shared.Enums
ModType.Prefix | Suffix                            // ExileCore.Shared.Enums, via mod.ModRecord.AffixType
ModPool.Explicit | Implicit | Enchanted | Any      // ExileCrafting
CraftSound.Succeeded | Failed | Error | Notification | QueueComplete
PromptButtons.YesNo | OkCancel
```
