# Using App Watcher with OpenClaw

OpenClaw support, as suggested by the client. OpenClaw follows the Agent Skills specification, so the two
skills in `skills/` load unchanged. What follows was checked against docs.openclaw.ai (skills page,
skills CLI page, ClawHub pages); where we could not verify a detail we say so.

## 1. Install the two skills

From this repository folder:

```powershell
openclaw skills install .\skills\app-watcher-analyze
openclaw skills install .\skills\app-watcher-ask
```

By default this installs into the active workspace's `skills/` folder; add `--global` to install into
the shared `~/.openclaw/skills` folder for every local agent. Copying the two folders by hand into the
workspace `skills/` folder works too: OpenClaw discovers any `SKILL.md` under its skill roots.
Run `openclaw skills list` to confirm both are loaded.

## 2. Point the workspace at the data folder, read-only

The skills need to read `C:\ProgramData\AppWatcher\data\<app-name>\`. Give the agent the path once, for
example in the workspace's notes or in the first message:

```
The App Watcher data folder is C:\ProgramData\AppWatcher\data\<app-name>. Read memory\ only.
```

(In the tray menu, "Copy data folder path for AI" puts this path on the clipboard.)

Keep it read-only in practice:

- The `app-watcher-ask` skill reads only `memory\*.md` and the latest `export\<date>\docs\raw\`.
- The `app-watcher-analyze` skill writes exactly one file, `memory\architecture-reviewed.md`.
- Neither skill opens `events\`, `effects\` or `snapshots\`, which hold raw activity and data.
- If your OpenClaw setup lets you restrict a workspace's file access, allow only that data folder.
  We did not verify the exact configuration keys for that on docs.openclaw.ai, so follow the OpenClaw
  documentation for your version.

## 3. Ask from chat

If the client's OpenClaw is connected to a chat tool (WhatsApp, Telegram, Slack), questions like
"what did users do in the app today" or "which screen is slowest" are answered by `app-watcher-ask`
from the memory files, with the confidence tags kept. We did not verify the chat connectors themselves;
they are OpenClaw features, not part of App Watcher.

## What is not claimed

- No ClawHub listing exists for these skills; install from this folder.
- Nothing here changes what App Watcher records. OpenClaw only reads the memory the learner wrote.
