// Name: Essence until a second modifier
// Description: An essence guarantees one mod - this spams it until a second one you want turns up alongside it

var essence = "Deafening Essence of Greed";
var wanted = new[] { "IncreasedPhysicalDamage" };

if (await ctx.ApplyCurrencyUntil(essence, () => ctx.CountMods(wanted) > 0))
{
    ctx.Log("got the guaranteed mod plus " + string.Join(", ", wanted));
    ctx.PlaySound(CraftSound.Notification);
}
else
{
    ctx.Log($"stopped - out of {essence}, or the run hit a limit");
    ctx.PlaySound(CraftSound.Error);
}
