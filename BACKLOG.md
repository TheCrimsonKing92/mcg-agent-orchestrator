# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Add prompt context budgets by role and file-access mode

Context: file-backed context reduces prompt bloat, but prompt size still depends on objective/task text, inline pointers, role requirements, and timelines. The next guardrail should make prompt budgets explicit instead of relying only on the large-paid-prompt guard.

Done condition: brief construction applies deterministic budget tiers by role and whether a context directory is available. When a file-access brief would exceed the role budget, lower-priority sections collapse to artifact pointers. Add tests proving late-pipeline file-access prompts stay below the budget while API/no-file prompts retain enough inline evidence.

## Design cautious local evidence retrieval, without MCP

Context: retrieval could help once context artifacts, dogfood logs, and prior evidence exceed simple file navigation, but it adds ranking/staleness risks. Do not implement MCP/resource-server plumbing. First design a local, read-only retrieval plan over existing artifacts and logs, with explicit trust boundaries and opt-in usage.

Done condition: add a short design document under `docs/` describing candidate indexed sources, ranking inputs, stale/unsafe context risks, when retrieval should be skipped, and the tests needed before implementation. No runtime retrieval subsystem should be added in this item.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.


