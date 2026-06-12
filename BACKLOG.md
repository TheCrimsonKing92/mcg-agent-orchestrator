# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.


## Allow re-delegating an Assigned task to the current role agent

Status: open | Size: small | Suggested route: simple-goal

Why: task assignment pins the agent id at delegation time, so replacing a role's catalog entry (for example swapping the Developer between Anthropic and OpenAI during a provider outage) orphans Assigned tasks: subscription-dispatch fails with "Assigned agent '<id>' was not found" and no command can re-point the task. Live recovery on goal eecf98f8 (2026-06-12) required restoring the old catalog entry and a manual worker-dispatch with literal model values.

Where: task delegation/assignment in AgentOrchestratorKernel, CLI surface near retry/delegate.

Done when: an operator command (a retry flag or a re-delegate command) reassigns an Assigned or Failed task to the catalog's current agent for its role, recording a timeline event; dispatch preparation then resolves the new agent normally.

Verify: focused tests for reassignment after a role catalog swap and unchanged behavior when the assigned agent still exists; full `dotnet test`.
