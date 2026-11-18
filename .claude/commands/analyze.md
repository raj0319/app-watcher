---
description: Architect review of an App Watcher data folder; writes memory/architecture-reviewed.md (thin wrapper around skills/app-watcher-analyze)
argument-hint: [path to data/<app-name>, optional]
---

Follow the skill in `skills/app-watcher-analyze/SKILL.md` of this repository exactly, with
`$ARGUMENTS` as the data folder path when it is given. The skill holds every step and rule; this
command only invokes it. Also keep the rules in CLAUDE.md: read only towards the watched app, stay
inside the data folder, no network.
