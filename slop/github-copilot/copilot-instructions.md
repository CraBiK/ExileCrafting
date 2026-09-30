# ExileCrafting

PoE1 ExileAPI overlay plugin (`net10.0-windows`, x64, ExileCore) that runs user-written C#
scripts to click currency onto an item or drive the horticrafting station.

There are no automated tests. Everything is verified in game by the player, so never describe a
change as tested. UI is ExileImGui only; if a component does not exist there, ask before
building a one-off. Few big files grouped by responsibility with `#region` sections. No code
comments except a one-line reminder where something is genuinely non-obvious. Never add an AI
co-author trailer to a commit. KISS, YAGNI, DRY.

## Reference

- `slop/reference/scripting-api.md` - the complete `ctx` API a craft script can call.
- `slop/reference/script-authoring.md` - script shape, guard clauses, worked examples, install
  and debug.
- `slop/reference/plugin-overview.md` - runtime prerequisites, the crafter window, settings,
  repo map, build.

## `.csx` files are craft scripts, not normal C#

They are Roslyn scripts compiled with `CraftContext` as the globals object, running top to bottom
with no class and no namespace. Read `slop/reference/scripting-api.md` before writing or editing
one - the imports and the awaiting rule are not guessable from the surrounding code. The detailed
rules are in `.github/instructions/exilecrafting-csx.instructions.md`.

The two rules that break a run silently if missed: only `await` a `ctx.*` call, and currency
names must be exact case-sensitive `BaseItemTypes.BaseName` strings taken from
`ExileCrafting/data/currency.txt`.
