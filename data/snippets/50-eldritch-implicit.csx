// Name: Eldritch Ichor until an implicit
// Description: Rerolls the eldritch implicit until it matches. Ichor is the red side, swap the name for Ember to roll the blue one

var ichor = "Lesser Eldritch Ichor";
var wanted = new[] { "Damage over Time Multiplier" };

while (await ctx.CheckMods(ModPool.Implicit, wanted) == 0)
{
    if (!await ctx.ApplyCurrency(ichor))
    {
        ctx.Log($"no {ichor} in the open stash tab or your inventory");
        ctx.PlaySound(CraftSound.Error);
        break;
    }
}
