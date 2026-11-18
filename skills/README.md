# Skills

Two portable skills in the open [Agent Skills](https://agentskills.io) format (a folder with a
`SKILL.md`). The same files work in Claude Code, OpenCode and OpenClaw.

| Skill | What it does |
|---|---|
| `app-watcher-analyze` | the architect review: reads memory and evidence, challenges weak findings, writes `memory/architecture-reviewed.md` |
| `app-watcher-ask` | answers plain questions about the watched app from the memory files only (also over chat tools OpenClaw is connected to) |

Both read only. `app-watcher-analyze` writes one file, `memory/architecture-reviewed.md`, inside the
App Watcher data folder. Neither touches the watched app's data.

## Install

**Claude Code** (verified: Claude Code loads skills from these folders)

```powershell
# this project only
Copy-Item -Recurse skills\app-watcher-analyze .claude\skills\
Copy-Item -Recurse skills\app-watcher-ask .claude\skills\
# or for every project of this user
Copy-Item -Recurse skills\app-watcher-* $env:USERPROFILE\.claude\skills\
```

This repository also ships `.claude/commands/analyze.md`, so `/analyze` works here without copying.

**OpenClaw** (verified on docs.openclaw.ai: skills load from `<workspace>/skills` and from the shared
`~/.openclaw/skills`; `openclaw skills install ./path` copies a local skill folder; `--global` targets the shared folder)

```powershell
openclaw skills install .\skills\app-watcher-analyze
openclaw skills install .\skills\app-watcher-ask
# or, for every local agent:
openclaw skills install .\skills\app-watcher-analyze --global
openclaw skills install .\skills\app-watcher-ask --global
# or simply copy the two folders into your workspace's skills\ folder
```

Publishing to ClawHub (`clawhub publish`) is possible but not done by this repository; the local install
above does not need ClawHub. See `openclaw/workspace-example.md` for pointing a workspace at the data folder.

**OpenCode** (not verified by us: the path `~/.config/opencode/skills` comes from the client's brief;
check your OpenCode version's documentation)

```powershell
Copy-Item -Recurse skills\app-watcher-* $env:USERPROFILE\.config\opencode\skills\
```

**Any other AI tool**: paste `skills/app-watcher-analyze/references/analyst-prompt.md` followed by the
`memory/*.md` files into the tool.

## Check the files against the standard

```bash
python scripts/check_skills.py
```

It checks the required fields (`name` matching the folder and the naming rules, `description` 1 to
1024 characters) and the optional `compatibility` length, as the specification states them.
The reference validator of the standard, `skills-ref validate ./skills/app-watcher-analyze`, can be
used instead when it is installed.
