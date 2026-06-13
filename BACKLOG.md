# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Add first-class recovery for dirty-but-useful dispatches

Context: goals `8d9409f6` and `482e8d1f` both had OpenAI/codex Developer workers produce correct edits and passing focused tests, then fail the dirty-worktree guard because no commit was created. The guard is correct, but recovery is manual: inspect diff, run tests, commit in the worktree, then `verify-manual`. Operators need a first-class recovery path that preserves the guard while reducing ceremony.

Done condition: when a Developer/Tester dispatch exits 0 with a dirty worktree and no commit, task details/next actions clearly label it as `dirty-useful` or equivalent, show changed files and verification evidence, and suggest an exact safe recovery command or workflow. Add focused tests for dirty-with-useful-output, dirty-without-verification, and clean/no-change paths. Do not auto-accept or auto-commit worker changes without an explicit operator action.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.


