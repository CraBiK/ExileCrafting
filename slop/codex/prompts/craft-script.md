Write an ExileCrafting craft script for: $ARGUMENTS

Steps:

1. Read `slop/reference/scripting-api.md` and `slop/reference/script-authoring.md`.
2. Look up every currency base name in `ExileCrafting/data/currency.txt` and every harvest craft
   line in `ExileCrafting/data/harvest-crafts.txt`. Do not write a name from memory. If the
   request needs a mod, say which string you are matching on and why, since matching is a
   squashed substring against a mod's Name, RawName and DisplayName.
3. If the request is ambiguous about the item base, the target mods, or how many of them are
   wanted, ask before writing.
4. Write the `.csx` with a `// Name:` / `// Description:` / `// Type:` header. Save it next to
   the other examples unless told otherwise.
5. Check it against the hard rules: only `ctx.*` is awaited, `*Until` conditions are synchronous
   and use `ctx.CountMods`, `ctx.Item` is never cached, nothing catches `CraftGuardException` or
   `OperationCanceledException`, only the pre-added imports are used, and a Harvest script says
   `// Type: Harvest`.
6. Tell the player what to have open in game before running it (which stash tab, which slot the
   item goes in, whether the horticrafting station is needed) and what the script will do when
   it cannot find something.

You cannot run or test it. Testing is Compile Check, then Run, then Copy log, all in game.
