// Name: Alteration until a modifier
// Description: Shift-spams Orb of Alteration until the item rolls one of the mods you name

var wanted = new[] { "IncreasedLife" };

if (ctx.Item.Rarity != ItemRarity.Magic)
{
    ctx.Log($"alteration needs a magic item, this one is {ctx.Item.Rarity}");
    ctx.PlaySound(CraftSound.Error);
}
else if (await ctx.ApplyCurrencyUntil("Orb of Alteration", () => ctx.CountMods(wanted) > 0))
{
    ctx.Log("got it: " + string.Join(", ", wanted));
    ctx.PlaySound(CraftSound.Notification);
}
