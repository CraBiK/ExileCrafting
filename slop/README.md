# slop

AI-agent config for this repo. Four agents, one set of reference docs.

Nothing in here is loaded automatically. Each agent only reads its own canonical path, so the
files have to be copied or junctioned there first. See Installing below.

## Layout

```
slop/
  reference/                                  the actual content, agent-agnostic
    scripting-api.md                          complete ctx API, types, enums, matching rules
    script-authoring.md                       script shape, rules, examples, install, debugging
    plugin-overview.md                        prerequisites, crafter window, settings, repo map
  claude-code/
    skills/exilecrafting-scripts/SKILL.md
  codex/
    AGENTS.md
    prompts/craft-script.md
  cursor/
    rules/exilecrafting-scripts.mdc
  github-copilot/
    copilot-instructions.md
    instructions/exilecrafting-csx.instructions.md
    prompts/new-craft-script.prompt.md
```

The four agent folders are thin adapters. They carry the eight rules that break a script when
missed, and point at `slop/reference/` for everything else. When a rule changes, it changes in
`slop/reference/` first and then in all four adapters - that duplication is deliberate, because
an agent that never opens the reference still needs the rules.

Reference paths inside the adapters are written relative to the repo root, so they keep resolving
wherever the adapter itself ends up.

## Installing

PowerShell, run from the repo root. A junction stays live as the files here change; a copy has to
be redone after every edit. Junctions do not need admin rights.

**Claude Code** - project skills are discovered from `.claude/skills/`:

```powershell
New-Item -ItemType Directory -Force .claude\skills
New-Item -ItemType Junction -Path .claude\skills\exilecrafting-scripts -Target slop\claude-code\skills\exilecrafting-scripts
```

**Codex** - `AGENTS.md` is read from the repo root, prompts are per-user:

```powershell
Copy-Item slop\codex\AGENTS.md AGENTS.md
Copy-Item slop\codex\prompts\craft-script.md "$env:USERPROFILE\.codex\prompts\craft-script.md"
```

If the repo already has an `AGENTS.md`, merge into it instead of overwriting.

**Cursor** - rules are discovered from `.cursor/rules/`:

```powershell
New-Item -ItemType Directory -Force .cursor\rules
Copy-Item slop\cursor\rules\exilecrafting-scripts.mdc .cursor\rules\
```

**GitHub Copilot** - all three paths live under `.github/`:

```powershell
New-Item -ItemType Directory -Force .github
Copy-Item slop\github-copilot\copilot-instructions.md .github\copilot-instructions.md
Copy-Item slop\github-copilot\instructions .github\instructions -Recurse -Force
Copy-Item slop\github-copilot\prompts .github\prompts -Recurse -Force
```

`instructions/*.instructions.md` needs `github.copilot.chat.codeGeneration.useInstructionFiles`
turned on, and prompt files need `chat.promptFiles`.

## If your agent is not one of these

Point it at `slop/reference/scripting-api.md` and `slop/reference/script-authoring.md` in
whatever always-on instruction file it reads. That is the whole content; the adapters add
nothing except trigger metadata and the rule summary.

The Claude Code `SKILL.md` is a plain markdown file with YAML frontmatter, so agents that have
adopted the same skill format can use it unchanged.
