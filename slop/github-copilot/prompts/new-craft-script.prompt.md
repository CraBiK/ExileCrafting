---
mode: agent
description: Write a new ExileCrafting craft script (.csx) from a plain-English description
---

Write an ExileCrafting craft script for what the user describes.

1. Read `slop/reference/scripting-api.md` and `slop/reference/script-authoring.md`.
2. Look up every currency base name in `ExileCrafting/data/currency.txt` and every harvest craft
   line in `ExileCrafting/data/harvest-crafts.txt`. Do not write a name from memory. Say which
   mod string you are matching on and why - matching is a squashed substring against a mod's
   Name, RawName and DisplayName, so a shorter fragment matches more.
3. Ask first if the item base, the target mods, or how many of them are wanted is unclear.
4. Write the `.csx` with a `// Name:` / `// Description:` / `// Type:` header.
5. Check it against the hard rules in
   `.github/instructions/exilecrafting-csx.instructions.md`: only `ctx.*` is awaited, `*Until`
   conditions are synchronous and use `ctx.CountMods`, `ctx.Item` is never cached, nothing
   catches `CraftGuardException` or `OperationCanceledException`, only the pre-added imports are
   used, and a Harvest script says `// Type: Harvest`.
6. Say what the player needs open in game before running it - which stash tab, which slot the
   item goes in, whether the horticrafting station is required - and what happens when the
   script cannot find something.

You cannot run or test it. Testing is Compile Check, then Run, then Copy log, all in game.
