# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Fix doctor executable detection for PowerShell env-prefixed worker profiles

Context: after configuring same-role alternates, root `doctor` correctly reported `alternate-ready=True` for all roles, but overall readiness stayed false because `qwen-code-cli` and `qwen-local` profiles were marked unresolvable. Their command templates begin with PowerShell environment assignments such as `$env:OPENAI_BASE_URL=...;`, and `OrchestratorHealthInspector.ExtractExecutable` treats that assignment as the executable instead of skipping to the real `qwen` command.

Done condition: `doctor`/health inspection parses PowerShell env-assignment prefixes and resolves the first real command after semicolon separators. Add focused tests for env-prefixed qwen profiles and keep existing echo/local command detection intact.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.


