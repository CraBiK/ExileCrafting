// Name: Shock Jewel
// Description: Bring a Cobalt Jewel to rare, then harvest-reforge it until it rolls Chance to Shock
// Type: Harvest

// ============================================================================================
// The bundled example, and the reference for everything a script can call.
//
// A script is plain C#. It runs top to bottom, and anything returning Task must be awaited.
// Only ever await a ctx call - awaiting Task.Delay or anything else resumes on the wrong thread
// and the next ctx call will throw saying so. To pause, loop on a ctx call instead.
//
// DOING THINGS (all async, all return false if the action could not be completed)
//   await ctx.ApplyCurrency("Orb of Alteration")
//       Picks the stack up and applies it once. Searches the open stash tab first, then your
//       inventory. Costs one application against Max uses, and its price against Max cost.
//   await ctx.ApplyCurrencyUntil("Orb of Alteration", () => ctx.CountMods("Life") > 0)
//       One pickup, then shift-clicks the same spot until the condition is true. Much faster
//       than a loop of ApplyCurrency, which walks the mouse back to the stack every time.
//       The condition is sync, so use CountMods in it. CheckMods is async and forcing it with
//       .Result throws "already has an operation in flight" - it wants the same executor that is
//       busy running this loop. No loss: CheckMods is CountMods plus a reading pause, and the
//       loop already waits Action delay between clicks.
//   await ctx.AlchScourUntil(() => ctx.CountMods("MapPlayerNoRegeneration") == 0)
//       Rolls a map without putting the orb down: one Orb of Alchemy pickup, then ALT swaps it
//       to an Orb of Scouring and back between clicks, so a miss is reset and rerolled without
//       the mouse ever going back to the stash. Needs both orbs in the tab. Each is counted and
//       priced separately. Stops on the condition, on running out, or on a limit.
//   await ctx.AlchScour()
//       One reroll: scours if the item is not white, then alchs once. Leaves it rare.
//   await ctx.ApplyPairUntil("Orb of Alchemy", "Orb of Scouring", useAlternate, done)
//       The general form. useAlternate() picks which of the two the next click needs, done()
//       stops the run. Any two currencies that ALT-swap on the cursor work here. The swap is
//       confirmed on the cursor before every click, so a wrong pairing stops the run rather
//       than clicking the wrong orb onto your item.
//   await ctx.ApplyHarvest("including a Lightning modifier")
//       Harvest scripts only. Filters the station's craft list by that text and presses Craft.
//       The text must match exactly one craft or it aborts and logs what it did match.
//       Costs one application against Max uses. Lifeforce has no price source, so it never
//       counts against Max cost.
//   await ctx.Ask("Keep this one?")                       -> true on Yes
//   await ctx.Ask("Overwrite?", PromptButtons.OkCancel)   -> true on OK
//       Puts a modal on screen and waits for you. The game keeps running. Closing it with the
//       X or Escape counts as No/Cancel. Your thinking time is not charged to Max run seconds.
//
// READING THE ITEM
//   ctx.Item.IsEmpty / .BaseName / .Rarity / .Mods / .Address
//       Read fresh from memory every time, so never cache it across an apply.
//       Rarity is ItemRarity.Normal / Magic / Rare / Unique.
//   ctx.CountMods("Life", "Mana")            -> how many of those names are on the item, now
//   await ctx.CheckMods("Life", "Mana")      -> same count, plus a human-sized pause to "read"
//       Prefer CheckMods for "does it have X yet" between applies. Both take an optional first
//       argument ModPool.Explicit (default) / Implicit / Enchanted / Any.
//   ctx.IsMod(mod, "Life")   -> does one ItemMod match that name
//   ctx.Squash("Master of Fire") -> "masteroffire", the normalising both of the above use
//
//   Name matching is loose: your text is squashed to letters and digits, then looked for inside
//   the mod's Name, RawName and DisplayName. A shorter fragment matches more.
//
// TELLING YOU THINGS
//   ctx.Log("text")          - a line in the craft log, kept in the run summary
//   ctx.SetStatus("text")    - the status line drawn on the game panel, overwritten as you go
//       Everything Log writes can be lifted out with the Copy log button at the bottom of the
//       craft panel, which is the quickest way to get a run's output into a message.
//   ctx.PlaySound(CraftSound.Error)
//       Error / Notification are yours to use. Succeeded / Failed / QueueComplete also fire on
//       their own. Each one is enabled and assigned a wav in the Sounds settings tab.
//
// RUN STATE (all read-only, useful for logging or deciding to stop early)
//   ctx.Applications / ctx.MaxApplications    applications used and the limit, 0 means no limit
//   ctx.SpentChaos   / ctx.MaxChaos           chaos spent and the budget, needs Ninja Price
//   ctx.Elapsed      / ctx.MaxSeconds         time on this item, excluding time spent in Ask
//   ctx.Used                                  name -> count of everything applied so far
//   ctx.UsageSummary()                        that dictionary as one readable line
//
// The three limits stop the run by throwing, which the plugin reports as a normal stop rather
// than an error - you do not need to check them yourself.
// ============================================================================================

var Reforge = "Reforge a Rare item with random modifiers, including a Lightning modifier";

var wanted = new[] { "Chance to Shock" };

// matched loosely: squashed to letters and digits, then looked for inside the mod's Name, RawName
// and DisplayName. if a reforge clearly rolled it and the script disagrees, read the "mods:" line
// it logs below and shorten this string to match. add more entries to want more mods.
var confirmed = false;
var listed = false;

while (true)
{
    if (ctx.Item.IsEmpty)
    {
        ctx.Log("nothing in the station's craft slot");
        ctx.PlaySound(CraftSound.Error);
        break;
    }

    if (ctx.Item.BaseName != "Cobalt Jewel")
    {
        ctx.Log($"wrong base: {ctx.Item.BaseName} - this one only handles Cobalt Jewels");
        ctx.PlaySound(CraftSound.Error);
        break;
    }

    var rarity = ctx.Item.Rarity;

    if (rarity == ItemRarity.Unique)
    {
        ctx.Log("unique, nothing to do here");
        ctx.PlaySound(CraftSound.Error);
        break;
    }

    // harvest reforge needs a rare, so walk it up first
    if (rarity == ItemRarity.Normal)
    {
        ctx.SetStatus("normal -> rare");
        if (!await ctx.ApplyCurrency("Orb of Alchemy"))
        {
            ctx.Log("no Orb of Alchemy in the open stash tab or your inventory");
            ctx.PlaySound(CraftSound.Error);
            break;
        }
        continue;
    }

    if (rarity == ItemRarity.Magic)
    {
        ctx.SetStatus("magic -> rare");
        if (!await ctx.ApplyCurrency("Regal Orb"))
        {
            ctx.Log("no Regal Orb in the open stash tab or your inventory");
            ctx.PlaySound(CraftSound.Error);
            break;
        }
        continue;
    }

    var found = await ctx.CheckMods(wanted);

    // once, so you can see the real mod text if the matching above needs adjusting
    if (!listed)
    {
        var mods = ctx.Item.Mods?.ExplicitMods;
        ctx.Log("mods: " + (mods == null || mods.Count == 0
            ? "none"
            : string.Join(", ", mods.Select(m => string.IsNullOrEmpty(m.DisplayName) ? m.Name : m.DisplayName))));
        listed = true;
    }

    if (found == 1)
    {
        ctx.Log("got Chance to Shock");
        ctx.PlaySound(CraftSound.Notification);
        break;
    }

    // lifeforce is the expensive part, so check before the first one rather than after twenty
    if (!confirmed)
    {
        confirmed = await ctx.Ask(
            "Rare Cobalt Jewel, no Chance to Shock on it yet.\n\n" +
            "Reforge with lightning until it rolls Chance to Shock?");

        if (!confirmed)
        {
            ctx.Log("stopped before the first reforge");
            break;
        }
    }

    ctx.SetStatus("no Chance to Shock yet");

    if (!await ctx.ApplyHarvest(Reforge))
    {
        ctx.Log("reforge didn't land - out of lifeforce, or that craft isn't in the station");
        ctx.PlaySound(CraftSound.Error);
        break;
    }
}
