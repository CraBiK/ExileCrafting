// Name: Abyss Jewel pair demo
// Description: Test bench for AltAugUntil and ChaosExaltUntil on an Abyss Jewel - magic runs alt+aug, rare runs chaos+exalt

// Both calls keep one stack on the cursor and ALT-swap to the partner orb for the extra mod, so
// watch the cursor while it runs: the orb under it should flip on its own, never go back to the tab.

// no mod names here on purpose - it rolls for affix shape, so any jewel works as a test subject.
// magic: alt until 1 prefix and no suffix, then aug. rare: chaos until 2 prefixes and 1 suffix,
// then exalt. either way the run stops once the swapped-in orb has actually landed.

// rare jewels cap at 4 explicits, gear at 6. this is what stops the exalt going onto a full jewel.
var jewelMaxMods = 4;

int Affixes(ModType type) => ctx.Item.Mods?.ExplicitMods?.Count(m => m.ModRecord?.AffixType == type) ?? 0;
int Prefixes() => Affixes(ModType.Prefix);
int Suffixes() => Affixes(ModType.Suffix);

// the swap is the thing being tested, so nothing counts as done until that orb goes down
bool Applied(string orb) => ctx.Used.ContainsKey(orb);

void ListMods()
{
    var mods = ctx.Item.Mods?.ExplicitMods;
    ctx.Log($"{ctx.Item.Rarity} {ctx.Item.BaseName}, {Prefixes()}p/{Suffixes()}s: " + (mods == null || mods.Count == 0
        ? "none"
        : string.Join(", ", mods.Select(m => string.IsNullOrEmpty(m.DisplayName) ? m.Name : m.DisplayName))));
}

if (ctx.Item.IsEmpty)
{
    ctx.Log("put an Abyss Jewel in the craft slot first");
    ctx.PlaySound(CraftSound.Error);
}
else if (!ctx.Item.BaseName.Contains("Eye Jewel"))
{
    ctx.Log($"wrong base: {ctx.Item.BaseName} - this demo wants an Abyss Jewel");
    ctx.PlaySound(CraftSound.Error);
}
else if (ctx.Item.Rarity == ItemRarity.Unique)
{
    ctx.Log("unique, nothing to roll here");
    ctx.PlaySound(CraftSound.Error);
}
else
{
    ListMods();

    // white can't take either pair, so walk it up to magic and test the cheap one
    if (ctx.Item.Rarity == ItemRarity.Normal && await ctx.Ask("White jewel.\n\nTransmute it and test alt + aug?"))
    {
        if (!await ctx.ApplyCurrency("Orb of Transmutation"))
            ctx.Log("no Orb of Transmutation in the open stash tab or your inventory");
    }

    if (ctx.Item.Rarity == ItemRarity.Magic)
    {
        ctx.SetStatus("alt + aug");
        ctx.Log("alt spam until 1 prefix and no suffix, then aug for the suffix");

        // a roll that comes up 1p/1s on its own is not the test - it keeps alting until the aug is
        // the thing that fills the second slot
        var ok = await ctx.AltAugUntil(
            () => Prefixes() == 1 && Suffixes() == 0,
            () => Applied("Orb of Augmentation"));

        ListMods();
        ctx.Log(ok ? "alt + aug: the aug landed" : "alt + aug: stopped short, see the line above");
        ctx.PlaySound(ok ? CraftSound.Notification : CraftSound.Error);
    }
    else if (ctx.Item.Rarity == ItemRarity.Rare)
    {
        // exalts are the expensive half of the demo, so it asks before the first one
        if (await ctx.Ask("Rare jewel.\n\nChaos spam it and exalt for the fourth mod?"))
        {
            ctx.SetStatus("chaos + exalt");
            ctx.Log("chaos until 2 prefixes and 1 suffix, then exalt for the fourth mod");

            var ok = await ctx.ChaosExaltUntil(
                () => Prefixes() == 2 && Suffixes() == 1,
                () => Applied("Exalted Orb"),
                jewelMaxMods);

            ListMods();
            ctx.Log(ok ? "chaos + exalt: the exalt landed" : "chaos + exalt: stopped short, see the line above");
            ctx.PlaySound(ok ? CraftSound.Notification : CraftSound.Error);
        }
    }

    ctx.Log(ctx.UsageSummary());
}
