// Name: Bring an item to rare
// Description: Alchemy from white, Regal from magic, leaves a rare alone

while (ctx.Item.Rarity == ItemRarity.Normal || ctx.Item.Rarity == ItemRarity.Magic)
{
    var orb = ctx.Item.Rarity == ItemRarity.Normal ? "Orb of Alchemy" : "Regal Orb";

    ctx.SetStatus($"{ctx.Item.Rarity} -> rare");

    if (!await ctx.ApplyCurrency(orb))
    {
        ctx.Log($"no {orb} in the open stash tab or your inventory");
        ctx.PlaySound(CraftSound.Error);
        break;
    }
}
