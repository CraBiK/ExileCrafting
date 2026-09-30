// Name: Alt spam, aug for the second mod
// Description: Alterations until the first mod, one alt-swapped Augment for the second, back to spamming if it misses

var first = new[] { "IncreasedLife" };
var second = new[] { "IncreasedMana" };

if (ctx.Item.Rarity != ItemRarity.Magic)
{
    ctx.Log($"alteration needs a magic item, this one is {ctx.Item.Rarity}");
    ctx.PlaySound(CraftSound.Error);
}
else if (await ctx.AltAugUntil(
             () => ctx.CountMods(first) > 0,
             () => ctx.CountMods(first) > 0 && ctx.CountMods(second) > 0))
{
    ctx.Log("got both");
    ctx.PlaySound(CraftSound.Notification);
}
else
{
    ctx.Log("stopped without the pair");
    ctx.PlaySound(CraftSound.Error);
}
