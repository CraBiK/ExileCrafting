# ExileCrafting plugin overview

An ExileAPI (ExileCore, Path of Exile 1) overlay plugin. It runs user-written C# scripts that
click currency onto an item, or drive the horticrafting station, until the item is what the
script wanted.

## What a run actually does

The plugin drives the real mouse and keyboard against the real game window. There is no memory
writing and no packet sending; a script that "applies an Orb of Alteration" right-clicks the
stack in the stash and left-clicks the item. Movement goes through a humanizer that curves the
path and randomises the delays, so run speed is a settings question, not a script question.

One run at a time. Everything is pumped one step per frame from `Tick`, so the HUD keeps
rendering while a script is suspended.

## Before a run can work

**Currency scripts**

- The stash must be open on the tab holding the currency. The stash tab is searched first, the
  player inventory second.
- The item goes in the **crafting slot**, which is cell x=28, y=0 of the currency stash - the
  slot the currency tab shows for applying orbs.
- Restricted Mode, when on, is an allowlist of currency stash cells that a search may take from.
  Cells are picked in game by hovering and pressing the toggle-slot hotkey. It only gates the
  stash, never the inventory.

**Harvest scripts**

- The horticrafting station window must be open, with the item in the station's craft slot.
- The script header must say `// Type: Harvest`.
- Currency for a harvest run still comes out of the open stash tab or the inventory.

**Optional**

- The Ninja Price plugin supplies chaos values. Without it every price reads 0, the cost column
  is empty, and a Max cost budget never trips.

## The crafter window

Default hotkey NumPad7.

| Control | What it does |
|---|---|
| Add Script | put a `.csx` from the scripts folder onto your list |
| New Script | open the built-in editor on a fresh template |
| Open Folder | explorer on the scripts folder, which is the plugin config directory's `Scripts` |
| Run / Edit / X | per row: start it, open it in the editor, drop it from the list |
| Max uses | applications before the run stops. 0 means no limit. Default 200 |
| Max cost | chaos budget for one run. 0 means no limit. Needs Ninja Price |
| Run Queue | run every queued item in order |
| Copy log / Copy summary | the run's output as text |

Queue an item by hovering an inventory cell and pressing the toggle-slot hotkey (default `+`),
then pressing it again to confirm the script. The queue moves each item into the craft slot,
runs its script, and puts it back in the exact cell it came from.

Queue rules worth knowing:

- Every job in one queue must be the same `Type`. Currency and Harvest drive different panels.
- The craft slot has to be empty before Run Queue, because the queue moves its own items.
- Items that finish (`done`, `limit`, `skipped`) leave the queue. Anything that failed, stuck or
  was cancelled stays, so Run Queue again is a retry of exactly what is left.

Abort is Escape by default and cancels the run outright. Pause is Space and holds after the
current action finishes, which is what keeps the mouse button and shift key from being stranded.

## Settings tabs

- **General** - enable, hotkeys, Restricted Mode, Max Run Seconds (default 120s, on).
- **Sounds** - five slots (Craft succeeded, Craft failed, Craft error, Craft notification, Queue
  complete), each a wav from the plugin's `sounds` folder plus a volume, and a throttle so two
  sounds never start on the same frame. Error and Notification only ever fire from
  `ctx.PlaySound`.
- **Automation** - the humanizer. Slow / Normal / Fast / Custom / Off presets over path shape
  (gravity, wind, max step, slow distance) and every delay (step, pre-click settle, click dwell,
  post-click settle, action, check mods, queue). Off moves the cursor straight to the target with
  no delays at all: fastest and the most obvious. Max step is the real dial for move speed;
  Action delay is the one a reroll loop pays per click.

## Repo map

Source lives in `ExileCrafting/`. Few big files, grouped by responsibility, sections marked with
`#region`.

| File | Holds |
|---|---|
| `ExileCraftingCore.cs` | plugin entry, run lifecycle, `Tick` / `Render`, start script and start queue |
| `ExileCraftingSettings.cs` | settings nodes plus the Sounds and Automation tabs |
| `CraftRun.cs` | `CraftContext` (the script API), the queue, the prompt, sounds, log and summary, crafting slot and inventory grid reads |
| `CraftUi.cs` | crafter dialog, overlay, progress bars, queue popover |
| `Scripting.cs` | `ScriptHost` (discovery, metadata, compile, load), autocomplete, the editor dialog and its picker panel |
| `ScriptSnippets.cs` | the mod / currency / harvest / snippet lists behind the editor's Insert buttons, and the `data/*.txt` reader |
| `ClickExecutor.cs` | the coroutine engine, humanized mouse, polling and confirmation helpers |
| `ClickExecutor.Routines.cs` | the craft actions themselves: pick up and apply, alt-swap pairs, drive the station, move items in and out |

Data that ships next to the dll: `data/currency.txt`, `data/harvest-crafts.txt`,
`data/snippets/*.csx`, `examples/*.csx`, `sounds/*.wav`. All are copied to output by the csproj.
Examples are seeded once into the user's scripts folder, and a deleted one stays deleted.

`ExileImGui/` is vendored, and it is the only UI toolkit used. If a component is missing, ask
before building a one-off.

## Building

`net10.0-windows`, x64, library. ExileCore / GameOffsets / ItemFilterLibrary are referenced
through the `exapiPackage` environment variable pointing at the HUD folder. Put the source under
the HUD's `Plugins/Source` (a directory junction works) and the HUD compiles it on start; do not
set output paths by hand.

There are no automated tests. Everything is verified in game by the player.

## Conventions when editing the plugin

- ExileImGui only for UI.
- No code comments except a one-line reminder where something is genuinely non-obvious. Never
  explain what the code does.
- Few big files, no line-count split rule.
- Commit only at the end of a completed piece of work, and propose the message first.
- KISS, YAGNI, DRY.
