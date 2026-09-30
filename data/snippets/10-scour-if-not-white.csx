// Name: Scour if it isn't white
// Description: Strips an item back to Normal before anything else touches it

if (ctx.Item.Rarity != ItemRarity.Normal && !await ctx.ApplyCurrency("Orb of Scouring"))
{
    ctx.Log("no Orb of Scouring in the open stash tab or your inventory");
    ctx.PlaySound(CraftSound.Error);
}
