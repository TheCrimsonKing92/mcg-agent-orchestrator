# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Make repo-scoped `.agents/skills` writable to subscription workers

Goals `9bd8e7bf`, `cd69d3ae`, and `318ddd1e` all failed before source changes because subscription workers could not write `.agents/**` inside their goal worktrees, and commits also failed because the worktree common `.git` object database is outside the worker sandbox writable root. Decide whether repo-scoped skills should be worker-authored; if yes, update sandbox/writable-root behavior or worktree/git setup so a worker can create `.agents/skills/<skill>/SKILL.md` and commit it without broadening access more than necessary. Done when a dogfood skill-authoring goal can create and commit a `.agents/skills/.../SKILL.md` file from a subscription worker, with a regression test or documented smoke check.

## Fix one-shot goal-targeted task command normalization

Interactive task commands accept goal-targeted forms such as `verify-manual <goal-prefix> <task-number> passed <note>` and `verify-manual --goal <goal-prefix> <task-number> passed <note>`, but one-shot `mcg-orchestrator.cmd` argument normalization still collapses the trailing task arguments incorrectly. During goals `fab431b5` and `2e387b6f`, operator recovery had to use the legacy current-goal form `verify-manual 1 ...`. Extend `CliArgumentParser.NormalizeArgs` to mirror `SplitCommand` task-target parsing for `retry`, `note`, `verification-plan`, `ask`, `verify`, `verify-manual`, `dispatch`, `worker-dispatch`, and `progress`. Done when tests cover one-shot goal-prefixed `verify-manual` and `retry` with multi-word trailing notes.


