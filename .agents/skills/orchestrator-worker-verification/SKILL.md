---
name: orchestrator-worker-verification
description: Verify subscription or API worker output in mcg-agent-orchestrator. Use when reviewing worker completions, dispatch logs, goal worktree diffs, dirty recovery, false-positive completion risk, no-file-change results, generated-noise changes, manual verification, retry decisions, or acceptance readiness.
---

# Orchestrator Worker Verification

Use this skill before trusting a worker result or accepting a goal.

## Verification Flow

1. Treat `Completed` status and final prose as claims, not proof.
2. Inspect the task record: assigned agent, dispatch command, process exit, heartbeat, stdout/stderr paths, and verification history.
3. Inspect the goal worktree:
   - `git status --short`
   - `git log --oneline <base>..HEAD`
   - `git diff --stat <base>..HEAD`
4. Compare worker claims against actual files, commits, tests, and commands.
5. Reject false-positive completions when the worker claims nonexistent files, endpoints, commands, or tests.
6. Ignore generated or irrelevant noise such as `.qwen/settings.json`, logs, scratch files, build output, and empty directories.
7. Run focused verification independently; broaden when shared code, CLI contracts, dashboard APIs, or worker policy changed.

## Decision Rules

- **Pass:** Relevant source change exists, claims match the diff, tests or manual checks support the objective, and no unrelated changes are present.
- **Retry:** The worker was blocked by provider limits, connectivity, permissions, missing context, or a fixable prompt gap.
- **Manual recovery:** The worker exposed useful evidence but did not produce an acceptable branch.
- **Reject:** The worker exits 0 but makes no relevant source change, changes only generated noise, or fabricates implementation details.

## Evidence To Record

When recording `verify-manual`, include:

- Commit id or explicit note that no commit exists.
- Files changed and why they satisfy the objective.
- Verification commands and pass/fail counts.
- Relevant dispatch/log/process evidence.
- `Model fit:` with model or launcher, task shape, and adequate/overkill/underpowered judgment.

## Acceptance Guard

Do not run `acceptance` until the branch is clean, the useful changes are committed, and the operator has reviewed both worker evidence and actual repository state.
