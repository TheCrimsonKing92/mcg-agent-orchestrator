---
name: orchestrator-dogfood
description: Operate mcg-agent-orchestrator self-improvement and dogfood goals. Use when creating or running simple-goal, goal, run-goal, lifecycle-simple-goal, workspace create/acceptance/remove, monitoring subscription validation, operator gates, dogfood-log updates, BACKLOG updates, or Model fit evidence for this repository.
---

# Orchestrator Dogfood

Use this skill when the task changes or validates the orchestrator by running work through the orchestrator itself.

> **Canonical operating guide: [`docs/operator-runbook.md`](../../../docs/operator-runbook.md).** The default path is the autonomous conductor (`conduct --loop --policy <...>`), which owns workspace creation, dispatch, acceptance, and cleanup — you do not call `workspace create` / `acceptance` / `workspace remove` by hand under it. Use `conduct --loop --daemon` only for a curated set of active goals that should be picked up after the loop starts; it is not a safe "drain the backlog" mode. The manual sequence below is a granular fallback. For stuck goals, use the runbook's stuck-goal playbook (`readiness`, `recover`, `attention show`/`attention answer` or `attention dismiss`).

## Operating Loop

1. Check the backlog with `backlog-list` before choosing or proposing work.
2. Create the goal: `simple-goal "<objective>"` (one Developer task) or `goal "<objective>"` (five-role pipeline). For long objectives use `--brief-file <path>`, then delete the file.
3. Drive it with the conductor — it owns workspace creation, dispatch, acceptance, and cleanup: `conduct --loop --watch --policy <Conservative|Permissive> --poll-seconds 15 --max-duration 5400`. Add `--daemon` only for controlled active-goal pickup; keep it bounded with `--max-duration` while the queue is still being proven.
4. Observe with `.\scripts\Invoke-RepoScript.ps1 scripts\Get-OrchestratorSnapshot.ps1 -GoalPrefix <goal-prefix>` for compact process/lock/status state, adding more goal prefixes as plain trailing arguments when needed, or `next <goal-prefix> --full` when you need full task detail. Treat task status as provisional until the acceptance gate passes — verify the diff/commits/tests independently (see the worker-verification skill).
5. When a goal sticks, use the runbook's **stuck-goal playbook**: `readiness <goal>` (start blockers), `recover <goal> "<note>"` (reset stuck/Failed/Cancelled tasks), `attention show <goal>` / `attention answer <goal> <id> "<text>"` or `attention dismiss <goal>` (clarifications).
6. At goal boundaries, record the `Model fit:` evidence and update the backlog (`backlog-close` / `backlog-add`).

The lower-level manual verbs (`workspace create` → `subscription-dispatch` → `start-dispatch` → `refresh-dispatch` → `acceptance` → `workspace remove`) remain for granular or fallback control only; under the conductor those steps are automatic. See [`docs/operator-runbook.md`](../../../docs/operator-runbook.md).

If a retry leaves a goal `Completed` while one or more tasks are still `Assigned`, try `recover` first. If the conductor remains blocked by the terminal status, the repo-bounded recovery helper is `.\scripts\Invoke-RepoScript.ps1 scripts\Set-OrchestratorGoalStatus.ps1 --status Active <goal>`; use it only as operator repair for that desync.

## Operator Gate

Before accepting a goal:

- Inspect changed files and confirm they match the objective.
- Confirm generated artifacts, logs, profiles, and scratch files are not accidentally committed.
- Run the narrowest meaningful tests; broaden when shared behavior changed.
- Check worker logs, exit code, heartbeat, and verification records.
- Include `Model fit:` with model/launcher, task shape, and whether it was adequate, overkill, or underpowered.

## Logs And Follow-Ups

- Record dogfood evidence with `dogfood-log add <goal-prefix>` and read it with `dogfood-log list --limit <n>`; durable entries live in `.orchestrator/dogfood-log.db`, not `DOGFOOD_LOG.md`.
- Keep entries short: goal id, objective/result, verification, blocker/friction, and Model fit.
- Close finished backlog items with `backlog-close` and add newly discovered follow-ups with `backlog-add` (the SQLite store, `.orchestrator/backlog.db`, is canonical).
- Treat backlog items as candidates. Use filtered `backlog-intake "<heading>" --create-simple-goal --backlog-coverage <full|slice>` or `backlog-intake "<heading>" --create-goal --backlog-coverage <full|slice>` for a small reviewed active set. Choose `full` only when the goal covers the complete source item; otherwise choose `slice` so remaining work stays Open. `goal-plan --create-*` also requires `--backlog-coverage <full|slice>`. Do not feed a stale backlog wholesale into daemon mode.
- For long acceptance/conductor runs, prefer `scripts/Start-OrchestratorCommand.ps1` through `scripts/Invoke-RepoScript.ps1`, then poll `scripts/Get-OrchestratorSnapshot.ps1`, `next <goal> --full`, and bounded log helpers instead of blocking the operator seat.
- Do not paste full prompts or long logs.

## Safety

- Do not run state-mutating orchestrator commands in parallel; sequence them to avoid lost state writes.
- Do not accept worker-reported success without independent branch/diff/test evidence.
- Use exact goal prefixes and task numbers for dispatch, refresh, verification, acceptance, and workspace cleanup.
