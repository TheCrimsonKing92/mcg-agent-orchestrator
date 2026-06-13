# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Treat provider connectivity failures as automatic failover candidates

During goal `9c21c03b`, `run-goal` stopped for manual retry when codex-cli failed before product work with websocket `os error 10013`, then stopped again when claude-cli failed with `API Error: Unable to connect to API (ConnectionRefused)`. Existing automatic failover handles usage limits and stall evidence, but not these provider-connectivity failures. Update dispatch failure classification and `RunGoalService` continuation so pre-work provider connectivity failures are recoverable when a same-role alternate subscription agent exists. Preserve the current safeguards: do not retry the same failed agent, cap automatic failovers per task, retain failure evidence, and stop with explicit guidance when no alternate remains. Done when focused tests cover both error shapes and a run-goal continuation test proves it re-delegates automatically.

## Reject false-positive worker completions with no relevant source change

During goal `9c21c03b`, qwen-code-cli exited 0 and claimed it added a Go `monitor` command against `/api/source-survey`, but the repo had no such files and the worktree had no requested source change. Strengthen completion validation for file-touching Developer tasks so exit code plus narrative is insufficient when the requested change is absent or only generated/noise files changed. Candidate signals: require a non-empty relevant diff or commit in the goal worktree, ignore known generated files such as `.qwen/settings.json`, and surface a `dirty-unverified`/manual-review state instead of `Completed` when evidence is inconsistent. Done when tests cover a zero-exit worker that changes only generated noise and a zero-exit worker that claims nonexistent files.


