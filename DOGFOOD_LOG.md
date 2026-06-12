# Dogfood Log

Entry convention: keep entries short and record only durable product signal. For subscription/API-authored work, add `Model fit: <model or launcher> - adequate|overkill|underpowered - <task shape> - <short reason>`.

Older entries are rotated to `docs/DOGFOOD_LOG-2026-06.md`. When this file grows past roughly 500 lines, move all but the most recent entries to a dated archive under `docs/`.

## 2026-06-12 - Subscription retry-after classification ignores quoted fixture text

The apparent OpenAI/gpt-5.5 Tester retry-after on goal `b8dc0816` was not real account exhaustion. The dispatch transcript contained a test fixture line quoting `ERROR: You've hit your usage limit ... try again at 4:58 PM`, and the classifier scanned the full stdout/stderr blob, so it parsed fixture text as provider stderr. `DispatchFailureClassifier` now considers only provider-shaped error lines (`ERROR:` or PowerShell native command wrappers such as `node.exe : ERROR:`) and parses retry-after from that same line. Regression tests cover both the real provider shape and the quoted `WorkerDispatchTests.cs:2509` transcript shape. Verification: focused DispatchExecutionTests 17/17; full suite Core 168/168 + Infrastructure 305/305.

- Friction: the bug blocked a simple-goal intended to fix itself (`78d45759`) before dispatch by reusing the contaminated retry metadata path; direct operator fix was appropriate because the orchestrator path was self-blocked.
- Model fit: operator/manual - adequate - root cause was local state/classifier logic, not model capability or subscription capacity.

## 2026-06-12 - Goal cancellation/supersession command shipped (goal d85cc946)

Goal `d85cc946`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli (~5.5 min). New `cancel-goal <goal-id> <reason> --confirm-goal-stop` and `supersede-goal <goal-id> <reason> --confirm-goal-stop` commands mark abandoned goals terminally `Cancelled` or `Superseded`, persist goal-level timeline reasons, refuse completed goals, and refuse goals with live dispatch processes until they are cancelled/refreshed. Dashboard/transcript display and next-action output now treat stopped goals coherently. Worker committed `e3e9cca`; operator follow-up `3d57831` added `.orchestrator-context/` to `.gitignore` after the dispatch guard correctly failed a dirty worktree containing only generated context artifacts. Acceptance fast-forwarded main and workspace removal succeeded.

- Operator gate: reviewed lifecycle/CLI/parser/rendering diffs; focused GoalLifecycleTests 21/21; focused CLI cancel-goal regression 1/1 after build-server shutdown; full worktree suite Core 172/172 + Infrastructure 306/306; acceptance verification passed.
- Friction closed: generated worker context artifacts no longer make file-role dispatches appear dirty.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped lifecycle command with tests; operator follow-up handled discovered generated-file hygiene.

## 2026-06-12 - Workspace commands can target non-current goals (goal efad9a91)

Goal `efad9a91`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli (~4 min). `workspace create|merge|remove [goal-id-prefix]` now resolves the optional goal id through the existing resolver and updates the command context to that goal, preserving current-goal behavior when no id is supplied. This closes the cleanup gap exposed immediately after `cancel-goal`/`supersede-goal`: `workspace remove 8b64de95` had targeted latest goal `d85cc946` instead of the stale goal's workspace. Commit `22e841f` fast-forwarded to main and workspace removal succeeded.

- Operator gate: reviewed CLI workspace diff and regression; focused GoalWorktreeTests 13/13 after one CS2012 build-server shutdown retry; full worktree suite Core 172/172 + Infrastructure 307/307; acceptance verification passed.
- Residual: superseded goal `8b64de95` has preserved uncommitted heartbeat-visibility edits in its worktree; do not remove that workspace until salvaged or explicitly discarded.
- Model fit: OpenAI/gpt-5.5 - adequate - small CLI targeting fix with focused regression.

## 2026-06-12 - Dispatch heartbeat status is operator-visible (goal c5a794cb)

Goal `c5a794cb`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli (~6.8 min). `ProcessLogReader` now reads the heartbeat sidecar with graceful missing/unreadable/invalid states, and CLI task details, CLI process logs, dashboard/API task detail DTOs, process log DTOs, work-summary process summaries, dashboard process rendering, and transcript output expose heartbeat path, availability, state, pid/child pid, last observed/progress timestamps, age/idle durations, and stdout/stderr byte counts. This salvaged the useful idea from superseded goal `8b64de95` without merging its stale branch. Commit `75ce187` fast-forwarded to main and workspace removal succeeded; the old `8b64de95` worktree and branch were removed after salvage.

- Operator gate: reviewed diff and worker log; focused heartbeat tests 5/5 after one CS2012 build-server shutdown retry; full worktree suite Core 172/172 + Infrastructure 311/311; `git diff --check` and acceptance verification passed.
- Friction closed: operators can now see heartbeat/progress data before the stall watchdog trips.
- Model fit: OpenAI/gpt-5.5 - adequate - broad but mechanical surface-wiring task with focused tests.

## 2026-06-12 - run-goal can fail over to configured alternate agents (goal e5e718ae)

Goal `e5e718ae`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli (~9.6 min). `RunGoalService` now detects recoverable subscription usage-limit evidence and provider-neutral heartbeat/progress stall evidence, then automatically re-delegates to an available unused same-role subscription-capable alternate agent from the active agent list and continues the normal subscription run. It does not invent hidden providers, does not loop back to failed agents, caps automatic failovers per task, preserves failure evidence, and stops with explicit guidance when no alternate exists. Commit `daee6ca` fast-forwarded to main and workspace removal succeeded.

- Operator gate: reviewed failover policy and tests; focused RunGoalService tests 10/10 after one CS2012 build-server shutdown retry; full worktree suite Core 172/172 + Infrastructure 315/315; `git diff --check` and acceptance verification passed.
- Residual: current saved catalog has only one implementation-role agent, so live failover requires configuring an alternate same-role agent first.
- Model fit: OpenAI/gpt-5.5 - adequate - policy-heavy run-loop change with focused hermetic tests.

## 2026-06-12 - CLI can add same-role alternate agents (goal 0af36831)

Goal `0af36831`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli. New `agent-add <role> <provider> <model> [name] [--complex-model <model>]` appends or replaces an agent by generated id without deleting the existing role primary, while `agent <role> ...` keeps its role-replacement behavior. Agent listing now marks primary vs alternate, catalog persistence round-trips same-role alternates, simple-goal activation still chooses the first available role agent, and run-goal failover can use a catalog-added alternate. Worker commit `b46a194` fast-forwarded to main and workspace removal succeeded.

- Operator gate: reviewed CLI/catalog/failover diffs; fixed an interactive parser gap so multi-word `agent-add` names are folded before flags in REPL mode too; focused tests 11/11 after one CS2012 build-server shutdown retry; full worktree suite Core 172/172 + Infrastructure 323/323; `git diff --check` and acceptance verification passed.
- Friction closed: live failover no longer requires hand-editing `agents.json` to make same-role alternates visible.
- Model fit: OpenAI/gpt-5.5 - adequate - small CLI/catalog extension with targeted tests; operator follow-up caught a REPL-only argument parsing gap.

## 2026-06-12 - Five-role run-goal validation and failover readiness doctor (goal 889baf96)

Configured same-role alternates with `agent-add` for all five roles, then ran a live `goal` through `run-goal --confirm-batch-start --confirm-large-paid-subscription-start`. Planner, Researcher, Developer, Tester, and Reviewer all completed; commit `af27b86` adds setup-doctor/health visibility for subscription failover readiness, including per-role `alternate-ready` and compact alternate id/name details in CLI and dashboard health output. Root `doctor` now reports all five configured roles with `alternate-ready=True`. Acceptance fast-forwarded main and workspace removal succeeded.

- Operator gate: reviewed health inspector, CLI, dashboard, and tests; patched alternate detection to use role ordering instead of reference identity; focused health/dashboard tests 82/82 after one CS2012 retry; full worktree suite Core 172/172 + Infrastructure 326/326; `git diff --check` and acceptance verification passed.
- Friction found: Tester produced valid verification evidence but was false-failed by the no-file-change guard because it was a verification-only task; operator added manual verification and recorded a backlog item to distinguish Tester verification-only success from file-touching expectations.
- Model fit: OpenAI/gpt-5.5 - adequate for Planner/Researcher/Developer/Tester on this narrow feature; Anthropic/claude-haiku-4-5 - adequate for Reviewer.

## 2026-06-12 - Verification-only Tester dispatches no longer need file changes (goal d6479545)

Goal `d6479545`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli. `BackgroundDispatchRunner` now keeps Developer unchanged dispatches strict, keeps Tester tasks that explicitly request file/test/source changes strict, and allows clean verification-only Tester dispatches when their output includes concrete passing verification evidence. Commit `90b52b9` fast-forwarded to main and workspace removal succeeded; the backlog item from goal `889baf96` is closed.

- Operator gate: reviewed dispatch guard and tests; tightened worker logic so merely mentioning `dotnet test` is not enough without passing/zero-exit evidence; focused BackgroundDispatchRunner tests 26/26 after one CS2012 retry; full worktree suite Core 172/172 + Infrastructure 329/329; `git diff --check` and acceptance verification passed.
- Model fit: OpenAI/gpt-5.5 - adequate - policy fix with hermetic dispatch evidence tests; operator follow-up tightened the verification-evidence predicate.

## 2026-06-12 - Doctor resolves env-prefixed qwen profiles (goal 0fec1d7c)

Goal `0fec1d7c`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli. `OrchestratorHealthInspector` now skips PowerShell setup statements such as `$env:...=...` and `Set-Location` before extracting the executable, so qwen profiles that set environment variables before invoking `qwen` are marked resolvable. Commit `27d1ac2` fast-forwarded to main and workspace removal succeeded; root `doctor` now reports `Ready: True`, both qwen profiles as `executable=qwen resolvable=True`, and all five roles as `alternate-ready=True`.

- Operator gate: reviewed executable parsing and tests; focused OrchestratorHealthInspector tests 17/17 after one CS2012 retry; full worktree suite Core 172/172 + Infrastructure 330/330; `git diff --check`, root `doctor`, and acceptance verification passed.
- Model fit: OpenAI/gpt-5.5 - adequate - small parser fix with narrow health-inspector coverage.
