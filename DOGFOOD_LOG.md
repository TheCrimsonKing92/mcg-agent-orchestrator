# Dogfood Log

Entry convention: keep entries short and record only durable product signal. For subscription/API-authored work, add `Model fit: <model or launcher> - adequate|overkill|underpowered - <task shape> - <short reason>`.

Older entries are rotated to `docs/DOGFOOD_LOG-2026-06.md`. When this file grows past roughly 500 lines, move all but the most recent entries to a dated archive under `docs/`.

## 2026-06-12 - Guarded lifecycle simple-goal command shipped (goal c1ce01cf)

Goal `c1ce01cf`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli. Added `lifecycle-simple-goal <objective>` to compose simple-goal creation, workspace creation, guarded `run-goal`, acceptance verification/merge, and workspace removal. The command requires `--confirm-batch-start` and `--confirm-large-paid-subscription-start`, preserves existing cost/acceptance/workspace/dirty guards, stops with a next command on failure, and does not add scheduling or overnight draining.

- Operator gate: reviewed CLI parser/lifecycle tests; focused lifecycle/parser tests 18/18 after one CS2012 build-server retry; full suite Core 198/198 + Infrastructure 346/346; `git diff --check`, acceptance verification, and workspace removal passed.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped CLI orchestration and tests with clean commit.

## 2026-06-12 - Dirty dispatch recovery guidance shipped (goal fa85fd15)

Goal `fa85fd15`, simple-goal Developer task. Codex/gpt-5.5 stalled and `run-goal` automatically re-delegated to the Claude Developer fallback, which completed commit `0403ad0`. Task details and next actions now classify dirty dispatch guard failures as `dirty-useful` or `dirty-unverified`, show changed files plus verification evidence when present, and point operators to `task N` for a safe numbered recovery workflow. The workflow still requires explicit operator inspection, test rerun, commit, and `verify-manual`; it does not auto-commit.

- Operator gate: reviewed and tightened worker output so suggested next action is a single safe `task N` command, fixed compile issues, ran focused recovery tests Core 10/10 and Infrastructure 4/4, full suite Core 198/198 + Infrastructure 341/341, `git diff --check`, and acceptance verification.
- Friction found: long Codex run timed out/stalled but automatic failover to Claude worked; model-fit note in manual verification overstated the Codex role, while the durable timeline correctly shows Claude completed the implementation.
- Model fit: Anthropic/claude-sonnet-4-6 - adequate - broad recovery UX implementation after Codex stall; OpenAI/gpt-5.5 - underpowered/stalled for this run.

## 2026-06-12 - Five-role validation after context economizing (goal 482e8d1f)

Goal `482e8d1f`, live five-role `goal` under `run-goal`, produced commit `4da8f34` adding subscription-plan prompt budget headroom display. Planner/Researcher ran read-only through OpenAI/gpt-5.5, Developer/Tester ran workspace-write through OpenAI/gpt-5.5, and Reviewer ran plan-mode through Anthropic/claude-haiku-4-5. Post-Developer prompt estimates stayed modest despite accumulated evidence: Tester `4294` chars and Reviewer `4333` chars, with no large-prompt confirmation required for the resumed run.

- Product result: `subscription-plan` ready-task lines keep `estPrompt=...chars` and now append `budget=...chars headroom=...chars` or `budget=...chars over=...chars`.
- Operator gate: Developer forgot to commit and hit the dirty-worktree guard; operator reviewed, ran focused CliSubscriptionPlan tests 4/4, full worktree suite Core 188/188 + Infrastructure 337/337, committed `4da8f34`, and recorded manual verification. Final acceptance passed and workspace removal succeeded.
- Friction found: dirty-but-useful worker recovery is still too manual; backlog item added for first-class recovery UX.
- Model fit: OpenAI/gpt-5.5 and Anthropic/claude-haiku-4-5 were adequate for this focused planning/implementation/test/review pipeline.

## 2026-06-12 - Local evidence retrieval design recorded

Added `docs/local-evidence-retrieval-design.md` as a design-only plan for cautious local retrieval over existing context artifacts, backlog, dogfood logs, and goal diffs. The design requires pointer-first output, provenance labels, role-aware ranking, skip conditions, and tests for ranking, staleness, injection safety, and prompt-budget behavior before any runtime implementation. No MCP, resource server, embeddings, background indexer, or worker dispatch behavior was added. The local-evidence-retrieval backlog item is closed.

- Operator gate: direct docs change only; reviewed against backlog done condition. No tests run because no runtime code changed.
- Model fit: local Codex session - adequate - design-only context-economics planning with web-informed retrieval/caching tradeoffs.

## 2026-06-12 - Prompt briefs enforce role and file-access budgets (goal d1e8ce01)

Goal `d1e8ce01`, Developer task, OpenAI/gpt-5.5 via codex-cli. `BuildTaskBrief` now applies deterministic character budgets by role and file-access mode, collapsing lower-priority file-access sections to context artifact pointers only when the rendered brief exceeds the role budget. `current-task.md` now carries current retry dispatch/verification evidence so collapsed sections point at an artifact with the needed detail. The prompt-context-budget backlog item is closed.

- Operator gate: reviewed prompt/context artifact diffs; focused TaskBriefTests 34/34; focused WorkerContextArtifacts/WorkerProfileDispatcher tests 4/4; full worktree suite Core 188/188 + Infrastructure 335/335.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped prompt budgeting and artifact coverage with focused regressions.

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

## 2026-06-12 - Context digest and non-length complexity signals shipped (goal a2bec209)

Goal `a2bec209`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli. `WorkerContextArtifacts` now writes `digest.md` beside objective/current-task/prior-evidence artifacts, `manifest.md` lists it, and file-access subscription briefs tell workers to read `digest.md` first while collapsing inline prior evidence to compact pointers. API/no-file briefs still retain richer inline prior evidence. `TaskComplexityEstimator` now scores action verbs, code-change intent, cross-surface impact, verification breadth, risk/safety, and role rather than leaning mainly on text length. Commit `d0e4ef9` fast-forwarded to main and workspace removal succeeded.

- Operator gate: reviewed context artifact, prompt, complexity, and tests; focused TaskBrief/TaskComplexity tests 54/54, focused WorkerProfileDispatcher tests 34/34, full worktree suite Core 176/176 + Infrastructure 331/331; `git diff --check` and acceptance verification passed.
- Friction found: worker final output line `Human input: none.` was parsed as a human-input request; operator answered it and recorded a backlog item to ignore explicit no-input lines.
- Model fit: OpenAI/gpt-5.5 - adequate - broad but cohesive context-economics slice with focused prompt-size and classifier tests.

## 2026-06-12 - Explicit no-input worker summaries are ignored (goal 8d9409f6)

Goal `8d9409f6`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli. `AgentOutputDirectives` now ignores exact no-input directive payloads such as `none`, `no`, `not needed`, and `no input needed` for both `HUMAN_INPUT:` and `Human input:` spellings, while preserving real question directives. Commit `74431c5` fast-forwarded to main and workspace removal succeeded; the no-human-input backlog item is closed.

- Operator gate: worker produced the correct edits and tests but failed the dirty-worktree guard because it did not commit; operator committed `74431c5`, recorded manual verification, and acceptance passed. Focused core directive/dispatch/API tests 60/60; full worktree suite Core 187/187 + Infrastructure 331/331; `git diff --check` passed.
- Model fit: OpenAI/gpt-5.5 - adequate - narrow parser fix with focused regressions; worker was capable but missed the required commit step.

## 2026-06-12 - Role-specific context summaries shipped (goal 76fbdfb0)

Goal `76fbdfb0`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli. Context artifacts now include `prior-task-summaries.md`, `manifest.md`/`digest.md` include role-specific artifact priorities, and file-access prompts point workers to compact summaries before full `prior-task-evidence.md`. Summaries extract changed files, behavior changes, verification command/result, risks, and model fit from prior verification evidence. Commit `332df12` fast-forwarded to main and workspace removal succeeded; no MCP or runtime retrieval subsystem was added.

- Operator gate: reviewed prompt/context/test diffs; focused TaskBrief tests 34/34, focused WorkerContextArtifacts/WorkerProfileDispatcher tests 37/37, full worktree suite Core 187/187 + Infrastructure 334/334; `git diff --check` and acceptance verification passed.
- Proof of context-economics improvement: later file-access prompts now carry artifact pointers and ordering instead of full prior stdout, while `prior-task-summaries.md` preserves the decision-changing prior evidence in files the worker can read on demand.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped context artifact and prompt-contract update with focused regression coverage.

## 2026-06-13 - CLI monitoring subscription consumer dogfooded (goal 9c21c03b)

Goal `9c21c03b`, simple-goal Developer task, was monitored through `/api/goals/9c21c03b/events/stream`; `.scratch/dogfood-monitor-stream.log` captured `goal.snapshot`, `timeline`, redelegation, completion, and keepalive events. Commit `eb42fc2` adds `monitor-goal <dashboard-url> <goal-id> [--since <event-id>] [--once]`, consuming snapshot and SSE monitoring endpoints with compact operator output, plus tests for parsing, formatting, and SSE framing. It also fixes the default `qwen-code-cli` template so `{subscriptionModelName}` is not double-quoted after substitution. Acceptance fast-forwarded main and workspace removal succeeded.

- Operator gate: worker providers failed before useful work or produced false completion, so operator recovered the goal in the worktree; focused monitor/profile tests 3/3, full Infrastructure 350/350, Core 198/198, and acceptance verification passed.
- Friction found: OpenAI codex-cli failed with websocket `os error 10013`, Claude failed with `ConnectionRefused`, and qwen3:8b claimed nonexistent Go files/endpoints while making no requested source change. Backlog now tracks connectivity failover and stronger worker-result validation.
- Model fit: OpenAI/Claude subscription workers - unavailable - connectivity failures before product work; qwen3:8b via qwen-code-cli - underpowered - false-positive/no-op for this repo feature; local Codex/operator - adequate - recovered implementation and verification.

## 2026-06-13 - First repo-scoped orchestrator skills authored in parallel goals

Goals `9bd8e7bf`, `cd69d3ae`, and `318ddd1e` created three repo-scoped Codex skills under `.agents/skills`: `orchestrator-dogfood`, `orchestrator-worker-verification`, and `dotnet-windows-build-hygiene`. Worker dispatches all hit the same sandbox boundary before source changes: `.agents/**` was not writable from subscription workers and worktree commits could not write to the common root `.git` object store. Operator recovered each goal in its worktree, committed `053c660`, `f3de622`, and `69cab4e`, recorded manual verification through the dashboard API, accepted all three goals, merged the non-fast-forward branches, and removed workspaces.

- Operator gate: root checks confirmed each `SKILL.md` has valid frontmatter and required trigger terms; Python `quick_validate.py` could not run because `python.exe` failed to start in this sandbox session. Root status clean after merges and workspace cleanup.
- Friction found: state-mutating orchestrator commands are unsafe to run in parallel; parallel `subscription-dispatch` lost prepared state for two goals, and parallel `workspace remove` hit CS2012/VBCSCompiler locks. Backlog now tracks serialization/locking, `.agents` worker writability, and goal-prefixed task command gaps.
- Model fit: OpenAI/gpt-5.5 - adequate - skill drafting was straightforward but blocked by filesystem/git permissions; local operator - adequate - manual recovery and verification.

## 2026-06-13 - False-positive worker completions rejected (goal fab431b5)

Goal `fab431b5`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli. `BackgroundDispatchRunner` now requires relevant post-dispatch file-change evidence for Developer completion and ignores generated/noise-only paths such as `.qwen/settings.json`; verification-only Tester behavior remains allowed. Worker produced useful edits and focused passing tests but exited dirty-useful without committing, so operator committed `75820a7`, recorded manual verification, accepted the goal, and removed the workspace.

- Operator gate: reviewed `BackgroundDispatchRunner` and `WorkerDispatchTests` diff; focused `BackgroundDispatchRunner` tests passed 28/28; acceptance verification passed.
- Friction found: one-shot CLI normalization still mishandles goal-targeted task commands with trailing notes; legacy current-goal form worked for recovery.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped dispatch validation policy change with focused tests; worker missed the commit requirement.

## 2026-06-13 - Provider connectivity failures can fail over (goal 2e387b6f)

Goal `2e387b6f`, simple-goal Developer task, OpenAI/gpt-5.5 via codex-cli. `DispatchFailureClassifier` now recognizes pre-work codex/claude provider connectivity failures including websocket OS error 10013, API connection failure, `ConnectionRefused`, DNS/host resolution, and transport refusal signals. `RunGoalService` routes that evidence through the existing same-role alternate failover path with preserved failure history and no-alternate guidance. Worker produced useful edits and focused passing tests but exited dirty-useful without committing, so operator hardened prompt-echo handling, committed `1a5ea9c`, recorded manual verification, accepted the goal, and removed the workspace.

- Operator gate: reviewed classifier/run-loop/tests; fixed useful-output detection so prompt text echoed on stderr does not suppress failover; focused `RunGoalService` tests passed 15/15 after one CS2012 build-server shutdown retry; acceptance verification passed.
- Friction found: same one-shot goal-targeted task command normalization gap as `fab431b5`; backlog now tracks it.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped failover/classifier change with focused run-loop tests; worker missed commit and needed operator hardening for stderr prompt echo.
