# AGENTS

Attention is scarce. Preserve it.

Prefer short, decision-changing output over comprehensive dumps. Show what changed, what is blocked, what was verified, and what needs a decision. Suppress everything else.

## Output Discipline

All commands must minimize output by default.

- Narrow by path, pattern, and glob before running commands. Do not rely on PowerShell pipeline caps such as `| Select-Object -First N`; they create extra Codex permission prompts.
- Prefer bounded `rg`/`rg --files` for discovery. Use exact paths or symbols once known.
- Exclude generated/noisy trees when surveying source using `-g "!**/bin/**"` style globs for `bin`, `obj`, `.scratch`, `.orchestrator-prototype`, `TestResults`, and `playwright-report`.
- Do not run broad repo-root `rg` unless the path and pattern are tight.
- Avoid `rg -C` until match count is known. Prefer `rg -n --count PATTERN path`, then inspect exact files/symbols.
- Do not dump full files unless known small. Prefer targeted search or narrow line windows.
- For `git diff`, use `git diff --stat` first, then inspect one file at a time.
- For build/test commands, use minimal verbosity and expand only failing output.
- For dashboard/API checks, use focused endpoints or helpers that return exact fields, not broad HTML/JSON/log payloads.
- If a command returns more than about 100 lines, stop, summarize the signal, and narrow the next command.

Good shape: `rg -n --count "DashboardHost" src/Mcg.AgentOrchestrator.App tests/Mcg.AgentOrchestrator.Infrastructure.Tests -g "!**/bin/**" -g "!**/obj/**"`

Bad shape: `rg -n "dashboard|goal|task|hosted|source-survey" .. -C 4`

## Retry and Loop Control

Before repeating a command, state what changed or what is being narrowed.

Do not repeatedly run broad searches, diffs, dashboard reads, browser scripts, build/test cycles, or dogfood actions hoping for a different result. If two attempts do not produce useful signal, switch strategy or ask for direction.

This repository implements an AI agent orchestrator. Avoid recursive or high-fanout behavior.

- Do not start Codex/orchestrator/subscription handoffs unless explicitly required.
- Do not run multiple agent tasks for work that can be inspected locally.
- Do not spawn workers to verify work until local evidence indicates the change is ready.
- Prefer one narrow verification command per change.

## Repository Rules

- Treat version-controlled files as source. Build outputs, browser profiles, prototype workspace files, logs, scratch scripts, and previous run artifacts are not source structure.
- Core and Infrastructure intentionally keep flat public namespaces: `Mcg.AgentOrchestrator.Core` and `Mcg.AgentOrchestrator.Infrastructure`.
- Do not split those namespaces unless there is a strong API reason and a migration plan for consumers.

## Dashboard / Dogfood Boundary

For ordinary implementation or debugging, inspect and edit source directly.

Use dashboard workflow controls only when the task explicitly involves dogfood validation, dashboard orchestration, subscription handoff behavior, or prototype workflow testing.

For live prototype dashboard work, prefer `.\mcg-orchestrator.cmd prototype-ui http://localhost:5087/ --refresh 5 --no-open`.

Keep prototype state isolated from repository state. Prototype dashboard state lives under `src/Mcg.AgentOrchestrator.App\.orchestrator-prototype\workspace`; launcher-backed task execution should run from the repository root through `MCG_ORCHESTRATOR_REPOSITORY_ROOT`.

When the task involves subscription/dogfood execution:

- Verify worker profiles before starting subscription tasks.
- Treat `Write-Output {promptPath}` profiles as echo-only, not real execution.
- After dispatch, confirm evidence, process logs, exit code, and verification records; never trust task status alone.
- When credits are constrained, inspect existing continuations/evidence/logs and cancel stale work before starting new agents.

## Safety

Use dashboard cancel/refresh controls or exact known process ids for stuck workers. Never run broad cleanup such as `Get-Process codex | Stop-Process`; it can kill the active Codex session.

On Windows, a running dashboard can lock app binaries. Prefer `.\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl http://localhost:5087/`.

Quote PowerShell test filters containing `|`, for example `--filter 'AgentCatalog|PrototypeWorkspaceSeeder|WorkerProfile|WorkerDispatch'`.

## Evidence

Update `DOGFOOD_LOG.md` only at dogfood goal boundaries or when recording durable product friction. Keep entries short: goal id, objective, command/action, exit code, focused result, blocker/friction, verification, next follow-up.

Do not paste full dashboard responses, full prompts, full logs, or long API payloads.
