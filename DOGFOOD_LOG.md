# Dogfood Log

Durable dogfood goal-boundary evidence now lives in `.orchestrator/dogfood-log.db`.

Use:

```powershell
mcg-orchestrator.cmd dogfood-log list --limit 10
mcg-orchestrator.cmd dogfood-log add <goal-prefix>
mcg-orchestrator.cmd record-goal <goal-prefix>
```

Do not append new durable entries to this tracked file. Historical rotated entries remain in `docs/DOGFOOD_LOG-2026-06.md`.
