---
name: orchestrator-dogfood
description: Operate mcg-agent-orchestrator self-improvement and dogfood goals. Use when creating or running simple-goal, goal, run-goal, lifecycle-simple-goal, workspace create/acceptance/remove, monitoring subscription validation, operator gates, DOGFOOD_LOG updates, BACKLOG updates, or Model fit evidence for this repository.
---

# Orchestrator Dogfood

Use this skill when the task changes or validates the orchestrator by running work through the orchestrator itself.

## Operating Loop

1. Check `BACKLOG.md` before choosing or proposing work.
2. Prefer `simple-goal "<objective>"` for one Developer task; use `goal "<objective>"` only when the five-role pipeline is the point of the validation.
3. Create an isolated workspace with `workspace create <goal-prefix>` before file-touching work.
4. Prepare subscription work with `subscription-dispatch <goal-prefix> <task-number>` or use `run-goal <goal-prefix> --confirm-batch-start` for lifecycle validation.
5. Start paid subscription work only after the explicit confirmation flags required by the command output.
6. Monitor with `monitor-goal <dashboard-url> <goal-prefix>` or focused commands such as `refresh-dispatch <goal-prefix> <task-number>` and work-summary endpoints.
7. Treat task status as provisional until the operator gate passes.
8. Run focused tests in the goal worktree, inspect the diff, and verify the branch commit.
9. Record `verify-manual <task-number> passed "<operator evidence incl. Model fit: ...>"` when recovering from worker gaps.
10. Run `acceptance <goal-prefix>` and then `workspace remove <goal-prefix>`.

## Operator Gate

Before accepting a goal:

- Inspect changed files and confirm they match the objective.
- Confirm generated artifacts, logs, profiles, and scratch files are not accidentally committed.
- Run the narrowest meaningful tests; broaden when shared behavior changed.
- Check worker logs, exit code, heartbeat, and verification records.
- Include `Model fit:` with model/launcher, task shape, and whether it was adequate, overkill, or underpowered.

## Logs And Follow-Ups

- Update `DOGFOOD_LOG.md` only at dogfood goal boundaries or for durable product friction.
- Keep entries short: goal id, objective/result, verification, blocker/friction, and Model fit.
- Remove closed `BACKLOG.md` items and add newly discovered follow-ups as self-contained entries.
- Do not paste full prompts, full dashboard payloads, or long logs.

## Safety

- Do not run state-mutating orchestrator commands in parallel; sequence them to avoid lost state writes.
- Do not accept worker-reported success without independent branch/diff/test evidence.
- Use exact goal prefixes and task numbers for dispatch, refresh, verification, acceptance, and workspace cleanup.
