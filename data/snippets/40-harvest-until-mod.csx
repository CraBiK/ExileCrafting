// Name: Harvest bench until a modifier
// Description: Repeats one horticrafting craft until the item rolls a mod you name. Needs '// Type: Harvest' in the script header

var craft = "Reforge a Rare item with random modifiers, including a Lightning modifier";
var wanted = new[] { "Chance to Shock" };

while (await ctx.CheckMods(wanted) == 0)
{
    if (!await ctx.ApplyHarvest(craft))
    {
        ctx.Log("the craft didn't land - out of lifeforce, or it isn't in the station");
        ctx.PlaySound(CraftSound.Error);
        break;
    }
}
