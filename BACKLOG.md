# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Fix no-file-change dispatch guard for verification-only Tester tasks

Context: live five-role goal `889baf96` completed Planner, Researcher, Developer, and Tester work, but `run-goal` stopped after Tester because `BackgroundDispatchRunner`/refresh classification failed a successful verification-only Tester dispatch with `Developer/Tester dispatch exited 0 but did not produce required file-change evidence`. The Tester output had strong evidence: focused `OrchestratorHealthInspector` tests 16/16 and full suite Core 172/172 + Infrastructure 326/326. Tester tasks often should not modify files; the guard should distinguish implementation/file-change expectations from verification-only work.

Done condition: successful Tester dispatches that run verification and leave the worktree clean are accepted when the task brief/role expects verification-only work, while Developer and genuinely file-touching Tester tasks still require file-change evidence or explicit operator review. Add focused tests for Developer unchanged failure, Tester verification-only success, and Tester expected-file-change failure.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.


