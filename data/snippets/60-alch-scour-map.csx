// Name: Alch and scour a map until the bad mods are gone
// Description: Rolls with Orb of Alchemy and scours anything carrying a mod you refuse to run

// mod ids, the same ones Insert Mod puts in. add every one you will not run.
var refuse = new[] { "MapPlayerNoRegeneration", "MapMonsterReflect" };

ctx.Log("rolling until none of: " + string.Join(", ", refuse));

if (await ctx.AlchScourUntil(() => ctx.CountMods(refuse) == 0))
{
    ctx.Log("clean roll");
    ctx.PlaySound(CraftSound.Notification);
}
else
{
    ctx.Log("stopped without a clean roll");
    ctx.PlaySound(CraftSound.Error);
}
