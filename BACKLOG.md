# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Treat provider connectivity failures as automatic failover candidates

During goal `9c21c03b`, `run-goal` stopped for manual retry when codex-cli failed before product work with websocket `os error 10013`, then stopped again when claude-cli failed with `API Error: Unable to connect to API (ConnectionRefused)`. Existing automatic failover handles usage limits and stall evidence, but not these provider-connectivity failures. Update dispatch failure classification and `RunGoalService` continuation so pre-work provider connectivity failures are recoverable when a same-role alternate subscription agent exists. Preserve the current safeguards: do not retry the same failed agent, cap automatic failovers per task, retain failure evidence, and stop with explicit guidance when no alternate remains. Done when focused tests cover both error shapes and a run-goal continuation test proves it re-delegates automatically.

## Reject false-positive worker completions with no relevant source change

During goal `9c21c03b`, qwen-code-cli exited 0 and claimed it added a Go `monitor` command against `/api/source-survey`, but the repo had no such files and the worktree had no requested source change. Strengthen completion validation for file-touching Developer tasks so exit code plus narrative is insufficient when the requested change is absent or only generated/noise files changed. Candidate signals: require a non-empty relevant diff or commit in the goal worktree, ignore known generated files such as `.qwen/settings.json`, and surface a `dirty-unverified`/manual-review state instead of `Completed` when evidence is inconsistent. Done when tests cover a zero-exit worker that changes only generated noise and a zero-exit worker that claims nonexistent files.

## Make repo-scoped `.agents/skills` writable to subscription workers

Goals `9bd8e7bf`, `cd69d3ae`, and `318ddd1e` all failed before source changes because subscription workers could not write `.agents/**` inside their goal worktrees, and commits also failed because the worktree common `.git` object database is outside the worker sandbox writable root. Decide whether repo-scoped skills should be worker-authored; if yes, update sandbox/writable-root behavior or worktree/git setup so a worker can create `.agents/skills/<skill>/SKILL.md` and commit it without broadening access more than necessary. Done when a dogfood skill-authoring goal can create and commit a `.agents/skills/.../SKILL.md` file from a subscription worker, with a regression test or documented smoke check.

## Serialize or lock orchestrator state-mutating commands

During the parallel skill-authoring run on 2026-06-13, running multiple `subscription-dispatch` commands in parallel lost prepared dispatch state for two goals, and running multiple `workspace remove` commands in parallel triggered CS2012/VBCSCompiler build locks. Add protection so state-mutating CLI/API operations either take an interprocess lock, reject concurrent mutations with a clear retry message, or use optimistic concurrency that cannot silently lose updates. Scope includes dispatch preparation/start/refresh, verification writes, human-input answers, acceptance, and workspace cleanup. Done when parallel mutation tests prove no state update is lost and the operator gets deterministic feedback.

## Add goal-prefix targeting to task-level CLI commands

Dispatch commands accept goal-prefixed forms such as `subscription-dispatch <goal-prefix> <task-number>`, but task-level commands such as `task`, `verify-manual`, `retry`, `note`, `verifications`, and `progress` still operate on the latest/current goal. During the parallel skill-authoring run, this forced dashboard API usage for goal-specific manual verification. Extend task-level commands to accept `--goal <goal-prefix>` or `<goal-prefix> <task-number>` consistently. Done when tests cover non-latest goal targeting for manual verification, retry, task display, and verification history.


