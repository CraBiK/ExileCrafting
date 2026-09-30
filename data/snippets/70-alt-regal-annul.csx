// Name: Alt spam, regal, annul
// Description: Alterations until a mod you want, Regal for a third, then optional annuls with a check after each one

var wanted = new[] { "IncreasedLife" };

if (!await ctx.ApplyCurrencyUntil("Orb of Alteration", () => ctx.CountMods(wanted) > 0))
{
    ctx.Log("never rolled it");
    ctx.PlaySound(CraftSound.Error);
}
else if (!await ctx.ApplyCurrency("Regal Orb"))
{
    ctx.Log("no Regal Orb in the open stash tab or your inventory");
    ctx.PlaySound(CraftSound.Error);
}
else
{
    // an annul can take the mod you were keeping, so it asks first and stops the moment it does
    while (await ctx.Ask("Annul a mod off this?"))
    {
        if (!await ctx.ApplyCurrency("Orb of Annulment"))
        {
            ctx.Log("no Orb of Annulment in the open stash tab or your inventory");
            break;
        }

        if (await ctx.CheckMods(wanted) == 0)
        {
            ctx.Log("the annul took the mod you wanted");
            ctx.PlaySound(CraftSound.Error);
            break;
        }
    }
}
