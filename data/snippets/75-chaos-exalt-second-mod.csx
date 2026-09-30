// Name: Chaos spam, exalt for one more mod
// Description: Chaos until the mod you want, one alt-swapped Exalt on top, back to spamming if the exalt misses

var first = new[] { "IncreasedLife" };
var second = new[] { "IncreasedMana" };

// 6 for gear, 4 for jewels
var maxMods = 6;

if (ctx.Item.Rarity != ItemRarity.Rare)
{
    ctx.Log($"chaos needs a rare, this one is {ctx.Item.Rarity}");
    ctx.PlaySound(CraftSound.Error);
}
else if (await ctx.ChaosExaltUntil(
             () => ctx.CountMods(first) > 0,
             () => ctx.CountMods(first) > 0 && ctx.CountMods(second) > 0,
             maxMods))
{
    ctx.Log("got both");
    ctx.PlaySound(CraftSound.Notification);
}
else
{
    ctx.Log("stopped without the pair");
    ctx.PlaySound(CraftSound.Error);
}
