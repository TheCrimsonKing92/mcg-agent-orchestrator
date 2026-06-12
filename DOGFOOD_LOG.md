# Dogfood Log

Entry convention: keep entries short and record only durable product signal. For subscription/API-authored work, add `Model fit: <model or launcher> - adequate|overkill|underpowered - <task shape> - <short reason>`.

Older entries are rotated to `docs/DOGFOOD_LOG-2026-06.md`. When this file grows past roughly 500 lines, move all but the most recent entries to a dated archive under `docs/`.

## 2026-06-11 - Five-role pipeline with haiku Reviewer: VBCSCompiler dispatch fix (goal 03192a9b)

Goal `03192a9b`, full Planner/Researcher/Developer/Tester/Reviewer pipeline, payload "worker-spawned VBCSCompiler instances hold worktree obj outputs". Merged (22d1f65, fast-forward), 414/414 green operator-verified - and the post-merge independent `dotnet test` succeeded FIRST TRY from repo root. Non-local dispatch wrappers now set `DOTNET_CLI_USE_MSBUILD_SERVER=0`, `MSBUILDDISABLENODEREUSE=1`, `UseSharedCompilation=false` before the worker command; 3 wrapper tests plus an env-verification e2e.

- First haiku Reviewer run (claude-haiku-4-5, `--permission-mode plan`): 67 seconds, reviewed the ACTUAL working tree - cited the new parameter, all three env vars, and the test inventory, and gave a production-readiness recommendation. Contrast with four file-blind qwen reviews; the haiku Reviewer is the new default, qwen remains the free fallback.
- First haiku Developer dispatch via the new Simple-complexity routing: implemented the wrapper change and tests correctly in 7.7 min but did NOT commit - operator committed at the gate. Haiku fidelity note: verify commit presence before acceptance on haiku-routed Developer tasks.
- Researcher (codex read-only) finished in ~3 min with a clean worktree - against 17-18 min plan-blind runs, the handoff file + embedded plan evidence plus the mechanical sandbox have ended Researcher overreach.
- Usage-limit handling validated live: two pre-reset dispatches auto-classified as recoverable with parsed retry-after; after 2 failures `subscription-dispatch` demands review with NO acknowledgement mechanism - working escape is `verify-manual <n> failed "<review note>"` (supersedes the limit-failure verification) then `retry` + dispatch. Product gap worth a small follow-up: an explicit `--confirm-limit-review` or similar.
- Model fit: Anthropic/claude-haiku-4-5 - adequate - plan-mode code review with file access - substantive, fast, cheap; also adequate for the scoped Developer change except the missed commit step.
- Model fit: OpenAI/gpt-5.5 - adequate - planner/researcher/tester roles - researcher stayed in role with evidence + sandbox; tester disciplined.

## 2026-06-11 - Context economics: handoff file + dead dashboard URLs removed from briefs (goal 2fff84fd)

Goal `2fff84fd` (CLI workspace), two sequential Developer tasks, claude-sonnet-4-6 (commits eb42b03, ffd9771; fast-forward merge). Per Miles: implement directly, no backlog filing. 411/411 green operator-verified.

- Task 1: dispatch preparation now writes `.orchestrator-handoff.md` into the dispatch working directory with FULL prior-task verification stdout (20k/task bound) for multi-task goals; the brief keeps the trimmed embedded section (ApiOnly parity) plus a pointer line. File is gitignored so worker commits never include it. Rationale: file-access workers get complete fidelity at zero prompt cost; prompt embeds stay orientation-sized.
- Task 2: briefs no longer point workers at `/api/goals/<prefix>/work-summary` and `/api/system/dashboard-host` - replaced with one line stating context is embedded plus the handoff file, and an explicit instruction not to reach dashboard APIs or orchestrator state (DOGFOOD blockers showed workers repeatedly burning turns on curl 000 and task-not-found attempts).
- Reviewer agent switched live to Anthropic/claude-haiku-4-5 with sonnet complex escalation (`agent reviewer Anthropic claude-haiku-4-5 --complex-model claude-sonnet-4-6`) per Miles - first haiku Reviewer dispatch will carry plan-mode read-only sandbox and real file access; the qwen ApiOnly reviewer remains a recreatable fallback.
- Operator-practice friction (2x today): first `workspace remove` fails when the operator shell's CWD sits inside the worktree from gate commands; rerun from repo root resumes cleanly. Practice: run gates as `dotnet test <worktree-path>` from root or cd back before removal.
- Model fit: Anthropic/claude-sonnet-4-6 - adequate - brief/dispatch plumbing with gitignore and test fixups - 2/2 first-attempt, including an unprompted cost-guard boundary check in task 2.

## 2026-06-11 - Cost routing: haiku-by-default for simple Anthropic work (goal 1c874f08)

Goal `1c874f08` (CLI workspace), one Developer task, claude-sonnet-4-6 (commit 28ade0e, fast-forward merge). The CLI `agent` command now accepts `--complex-model <model>` (reusing the dashboard submission path's existing complex-model fields), and a new `AgentCatalog.AnthropicDefault()` pairs claude-haiku-4-5 base with claude-sonnet-4-6 complex across all five roles. With `TaskComplexityEstimator.ResolveModel` already escalating on Complex classification or unresolved underpowered fit notes, Simple-classified Anthropic dispatches now route to haiku automatically. 407/407 green, operator-verified independently (worker count matched this time).

- Live catalog updated post-merge: `agent developer Anthropic claude-haiku-4-5 --complex-model claude-sonnet-4-6`. Watch the next several Simple dispatches for haiku fit notes; underpowered evidence auto-escalates to sonnet by design.
- Friction: `workspace remove` failed once because the operator shell's working directory still sat inside the worktree; rerun from repo root resumed and completed (second live validation of 9c716c0 resumable removal).
- Friction (now 4x today): VBCSCompiler CS2012 lock on first operator `dotnet test` in every goal worktree; the post-dispatch shutdown from e7f0a64 does not cover compiler instances spawned by the worker's own test runs. Consider `-p:UseSharedCompilation=false` in worker brief guidance or wrapper environment.
- Model fit: Anthropic/claude-sonnet-4-6 - adequate - CLI option + catalog defaults + tests on existing patterns - first-attempt pass; worker self-noted the shape was haiku-eligible, fitting since this goal enables exactly that routing.

## 2026-06-11 - Reviewer diff evidence + paid output caps raised (goal d729e13c)

Goal `d729e13c` (CLI workspace), two sequential Developer tasks, claude-sonnet-4-6. ApiOnly prompts now include a `## Workspace Diff` section (commit 3c45e62): `AgentTaskRunner` takes an optional `Func<GoalId, string?>` diff provider, `GoalWorktrees.TryGetBranchDiff` supplies stat+patch of `main...HEAD` from the goal worktree, trimmed to 3200 chars with marker; wired at all three CLI/dashboard construction sites. Paid output caps raised 768->2048 routine / 1200->4096 complex on 4/4 over-cap evidence (commit 3d906c6); resolved backlog entry removed. Acceptance fast-forwarded; 403/403 green after operator fix.

- Operator gate caught a false worker verification: task 2's worker claimed full `dotnet test` passed, but the independent run failed 3 tests - it raised only `AgentTaskRunner`'s private constants and missed the duplicated policy mirrors `AgentCatalog.RoutineApiMaxOutputTokens`/`ComplexApiMaxOutputTokens` (provider request defaults, dashboard placeholders) plus literal `768`/`1200` assertions in ProviderIntegrationTests/DashboardRenderingTests. Operator fixed directly on the goal branch (commit 13f2c38) rather than paying a redispatch for a 6-line alignment. Product lesson: duplicated policy constants invite exactly this miss - candidate cleanup item; worker test claims remain untrustworthy without the independent gate.
- Worker wall times 6 min and 7.5 min. VBCSCompiler CS2012 lock hit twice during operator verification; shutdown + retry ritual cleared both.
- Next validation: a five-role pipeline run after codex resets should show the qwen Reviewer citing actual diff content instead of objective text.
- Model fit: Anthropic/claude-sonnet-4-6 - adequate - DI seam + formatter budgeting (task 1 flawless) - but task 2's "precise test impact analysis" self-assessment was wrong; 11/12 first-attempt on scoped briefs.

## 2026-06-11 - Refresh-dispatch hardened: locked-log tolerance + bounded snippets (goal f5675269)

Goal `f5675269` (CLI workspace), two sequential Developer tasks in one worktree, claude-sonnet-4-6 via subscription-dispatch. Closes the "Refresh-dispatch fails when live stdout log is locked" (commit 858dd62) and "Bound refresh-dispatch output for long subscription logs" (commit e2638a2) backlog entries; acceptance fast-forwarded, 399/399 green operator-verified.

- Task 1 (5 min): `ReadBestEffort` opens logs with `FileShare.ReadWrite | FileShare.Delete` so refresh reads alongside a live wrapper write handle; IO/access failures degrade to a path-bearing marker instead of throwing; timeout/exit-file handling proceeds. 3 tests.
- Task 2 (9.5 min): verification snapshots carry `StandardOutputPath`/`StandardErrorPath`; CLI/dashboard verification views render through new `OutputTextPreview.CreateVerificationLog` (2000-char cap, 400-char tail, marker includes the full log path). 4 tests. Workers were told to remove their BACKLOG entries in-commit and both did - no operator backlog toil this goal.
- Loop quality-of-life observed live: both `refresh-dispatch` calls completed first-try with no locked-log `cancel-dispatch`/`verify-manual` dance (worker exit + wrapper cleanup sufficed this run); the multi-task pattern again coordinated cleanly via prior-task evidence ("build on it, do not revert" honored).
- Friction (repeat): operator's first independent `dotnet test` in the worktree hit the VBCSCompiler CS2012 lock (PID 59316) despite the post-dispatch `dotnet build-server shutdown` from e7f0a64 - with two sequential workers the second worker's compiler instance survived. Ritual shutdown + retry cleared it.
- Model fit: Anthropic/claude-sonnet-4-6 - adequate - scoped infra hardening with explicit file pointers and done-conditions - 2/2 first-attempt passes, 10/10 lifetime for sonnet on scoped briefs.

## 2026-06-11 - Firewall prompt root cause: dotnet-run apphost spawn in the hosted e2e test (already fixed by f126524)

Operator-direct forensics with Miles; closes the "Avoid repeated Windows firewall prompts" backlog entry. No code change needed - the fix already landed.

- Root cause: e8b8a33 (06-11 10:08) added the simple-hosted-dashboard e2e test using the old spawn helper, which launched the dashboard via `dotnet run --no-build --project <App.csproj>` - that runs the per-worktree apphost `Mcg.AgentOrchestrator.App.exe`, and `simple-hosted-dashboard` binds `0.0.0.0`. Each goal worktree's first `dotnet test` therefore listened on a wildcard socket from a brand-new exe path, and Windows Firewall prompts per exe path.
- Evidence: firewall event log (event 2097 "Query User" rule pairs) shows prompts at 10:25, 11:02, 11:15, 12:22, 12:46, 13:07 local on 06-11 - one per goal worktree (9d78b9c1, 7646c03c, 2c8af0da, d47ec298, a1f3368f, bc128354), each inside that goal's worker dispatch window. f126524 (06-11 14:02) switched the spawn to `dotnet <App.dll>`, so the socket owner is `dotnet.exe`, already covered by the standing ".NET Host" allow rules. No prompts after 13:07.
- Verification: fresh throwaway worktree, full build + 233 Infrastructure tests (including both Kestrel e2e spawns) with a socket monitor polling for worktree-owned listeners - zero firewall events, zero prompts, zero wildcard listeners attributed to worktree processes.
- Residual: genuine hosted dashboard launches from new exe paths (worktree `dotnet run`/published exe) still prompt once - that is real LAN serving, working as designed. One-time elevated setup for unattended hosted use: `New-NetFirewallRule -DisplayName "MCG Orchestrator hosted dashboard" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5087-5186 -Profile Private`. Twelve stale "Query User" rules for deleted worktree paths remain; remove with an elevated `Get-NetFirewallApplicationFilter | Where-Object { $_.Program -like '*mcg-agent-orchestrator*' -and -not (Test-Path $_.Program) } | Get-NetFirewallRule | Remove-NetFirewallRule`.
- Note: parse-time port probing (`CanBindAnyIPv4Port`) also performs a real transient `0.0.0.0` listen for hosted commands; it rides on whichever process runs it and needs no separate fix while spawns stay on `dotnet.exe`.

## 2026-06-11 - Simple hosted dashboard goal task 126009e6df0f40aba2f1c15829a4526d

- Goal/task: hosted Developer task `126009e6df0f40aba2f1c15829a4526d`, objective `Simple hosted dashboard goal`.
- Changed files: `DOGFOOD_LOG.md` only for this evidence entry. No product source edits were required because the simple hosted dashboard behavior is already implemented and covered.
- Source inspection: `/api/goals/c9417723/work-summary` on `localhost:5087` was unreachable from this worker; focused local search confirmed the simple hosted dashboard tests and prior evidence entries.
- Verification: `dotnet build tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-restore --verbosity:minimal` exited `0`. `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter 'SimpleHostedDashboardServesReadOnlyMetadataAndSurvey|SourceSurveyLimitsReturnedFilesButReportsTotalMatches|SourceSurveyExcludesGeneratedScratchAndPrototypeState' --results-directory .\TestResults --logger 'trx;LogFileName=simple-hosted-dashboard-task-126009e6-rerun.trx'` exited `0`; output reported `Passed: 3`, `Failed: 0`.
- Blockers and direct interventions: live hosted dashboard API was not reachable on `localhost:5087`, so completion could not be posted through dashboard controls from this worker.
- Model fit: OpenAI/gpt-5.5 - adequate - hosted dashboard evidence task - enough for focused inspection and verification without product edits.

## 2026-06-09 - Simple hosted dashboard goal task b2fc4e8cc54845f38d60886984b31598

- Goal/task: delegated Developer task `b2fc4e8cc54845f38d60886984b31598`, objective `Simple hosted dashboard goal`.
- Changed files: `DOGFOOD_LOG.md` only for this evidence entry. No product source edits were required because the simple hosted dashboard behavior is already implemented and covered.
- Source inspection: `rg -n "Prototype_dashboard_serves_health_and_goal_json_over_kestrel|SourceSurvey|simple-hosted-dashboard|Simple hosted dashboard goal|EnableOperatorControls|read-only" ..\..\tests Dashboard Program.cs -g "!**/bin/**" -g "!**/obj/**" -g "!**/.scratch/**" -g "!**/.orchestrator-prototype/**"` exited `0`; evidence found `Program.cs` advertising `simple-hosted-dashboard`, `DashboardHost` setting `EnableOperatorControls` false for that command, read-only endpoint responses in `DashboardEndpoints`, `/api/source-survey` mapping, and the Kestrel test posting `Simple hosted dashboard goal`.
- Verification: `dotnet test ..\..\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-restore --filter 'SourceSurveyTests|DashboardHostTests' --results-directory .\TestResults --logger 'trx;LogFileName=simple-hosted-dashboard-task-b2fc4e8c.trx'` exited `0`; TRX `TestResults\simple-hosted-dashboard-task-b2fc4e8c.trx` records `total="4"`, `executed="4"`, `passed="4"`, and `failed="0"`.
- Blockers and direct interventions: `curl.exe --noproxy "*" --max-time 5 -s -o NUL -w "%{http_code}" http://127.0.0.1:5087/dashboard` returned `000`, so the live dashboard API was not reachable from this worker and task completion could not be posted through dashboard controls.

## 2026-06-09 - Simple hosted dashboard goal task 58cec8b9a48041c4acbb882cfe9691e4

- Goal/task: delegated Developer task `58cec8b9a48041c4acbb882cfe9691e4`, objective `Simple hosted dashboard goal`.
- Changed files: `DOGFOOD_LOG.md` only for this evidence entry. No product source edits were required because the brief did not request a new behavior beyond verifying the simple hosted dashboard path.
- Source inspection: `rg --files -g "!**/bin/**" -g "!**/obj/**" -g "!**/.scratch/**" -g "!**/.orchestrator-prototype/**"` and `rg -n "source-survey|work-summary|DOGFOOD_LOG|Simple hosted dashboard|complete-task|Invoke-DashboardDogfoodAction"` confirmed the app exposes `/api/source-survey`, goal work summaries, and `simple-hosted-dashboard` routing in `Program.cs` and `DashboardHost`.
- Verification: `dotnet build .\Mcg.AgentOrchestrator.App.csproj --no-restore` exited `0` after a transient apphost lock cleared; output reported `Build succeeded`, `0 Warning(s)`, and `0 Error(s)`.
- Hosted smoke: `.\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.exe simple-hosted-dashboard http://localhost:5087/ --refresh 5 --no-open` reached the read-only startup banner before the bounded timeout: bind URL `http://0.0.0.0:5087/`, dashboard page `http://localhost:5087/dashboard`, hosted pages `http://192.168.1.214:5087/dashboard` and `http://seventhson:5087/dashboard`, and source survey `http://localhost:5087/api/source-survey`.
- Blockers and direct interventions: background process launch via `Start-Process` and .NET `Process.Start` was rejected by sandbox policy, so endpoint polling could not run concurrently with the hosted process. Exact stale app PIDs from the smoke were stopped when visible; process metadata queries through CIM were denied.

## 2026-06-09 - Simple hosted dashboard goal task 39b2f6d186ed4034bd12d312e06e44d7

- Goal/task: delegated Developer task `39b2f6d186ed4034bd12d312e06e44d7`, objective `Simple hosted dashboard goal`.
- Changed files: `DOGFOOD_LOG.md` only for this evidence entry. No product source edits were required because the existing simple hosted dashboard behavior already matches the task objective.
- Source inspection: `rg -n "Prototype_dashboard_serves_health_and_goal_json_over_kestrel|SourceSurvey|simple-hosted-dashboard|Simple hosted dashboard goal|complete-verify" ..\..\tests Dashboard Program.cs -g "!**/bin/**" -g "!**/obj/**" -g "!**/.scratch/**" -g "!**/.orchestrator-prototype/**"` exited `0` and confirmed `Program.cs` advertises `simple-hosted-dashboard`, `DashboardHost` disables operator controls for it, `/api/source-survey` is mapped, and `DashboardHostTests` posts `Simple hosted dashboard goal` through Kestrel.
- Verification: `dotnet test ..\..\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-restore --filter 'SourceSurveyTests|DashboardHostTests' --results-directory .\TestResults --logger 'trx;LogFileName=simple-hosted-dashboard-task-39b2f6d.trx'` exited `0`; TRX `TestResults\simple-hosted-dashboard-task-39b2f6d.trx` records `total="4"`, `executed="4"`, `passed="4"`, and `failed="0"`.
- Blockers and direct interventions: `..\..\scripts\Invoke-DashboardApi.ps1 -Path api/source-survey`, `..\..\scripts\Invoke-DashboardApi.ps1 -Path api/goals/39b2f6d186ed4034bd12d312e06e44d7/work-summary`, and `curl.exe --noproxy "*" --max-time 5 -s -o NUL -w "%{http_code}" http://127.0.0.1:5087/dashboard` could not reach the live dashboard (`curl` returned `000`), so task completion could not be posted through the dashboard API from this worker.

## 2026-06-09 - Simple hosted dashboard goal task f710c0f779a7438f969781e05ad3941f

- Goal/task: delegated Developer task `f710c0f779a7438f969781e05ad3941f`, objective `Simple hosted dashboard goal`.
- Changed files: `DOGFOOD_LOG.md` only for this evidence entry. No product source edits were required because the simple hosted dashboard path is already implemented.
- Source inspection: focused reads of `Program.cs`, `Dashboard\Hosting\DashboardHost.cs`, `Dashboard\Api\DashboardEndpoints.cs`, `Orchestration\SourceSurvey.cs`, and `..\..\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\DashboardHostTests.cs` confirmed `simple-hosted-dashboard` is advertised/routed, binds hosted URLs, disables operator controls, maps `/api/source-survey`, and returns read-only responses for mutating API calls.
- Verification: `dotnet test ..\..\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter 'DashboardHostTests|SourceSurveyTests' --logger 'trx;LogFileName=f710-simple-hosted-dashboard.trx' --results-directory artifacts\verify-test-results\f710` exited `0`; TRX `artifacts\verify-test-results\f710\f710-simple-hosted-dashboard.trx` records `total="4"`, `executed="4"`, `passed="4"`, and `failed="0"`.
- Hosted smoke: `dotnet run --no-build -- simple-hosted-dashboard 5100 --no-open` served `http://127.0.0.1:5100/health` with `ok`, reported `Dashboard mode: read-only simple hosted view`, returned `/api/source-survey?max=5` with `returnedFiles=5` and `totalMatchedFiles=85`, and returned HTTP `403` for mutating `POST /api/goals`. The exact smoke PID `112968` was stopped; `netstat -ano | Select-String ':5100'` returned no listener afterward.
- Blockers and direct interventions: `..\..\mcg-orchestrator.cmd task f710c0f779a7438f969781e05ad3941f` and `dotnet run --no-build -- task f710c0f779a7438f969781e05ad3941f` both reported the task id was not found in local `.orchestrator\state.json`, so completion could not be recorded through the local dashboard state. `..\..\mcg-orchestrator.cmd` also attempted a build and was blocked by sandbox write denial to `src\Mcg.AgentOrchestrator.Core\obj`.

## 2026-06-09 - Simple hosted dashboard goal task 4be2c0329c064a60a19d72e753500af1

- Goal/task: delegated Developer task `4be2c0329c064a60a19d72e753500af1`, objective `Simple hosted dashboard goal`.
- Changed files: `DOGFOOD_LOG.md` only for this evidence entry. No product source edits were required because the simple hosted dashboard path is already implemented.
- Source inspection: `rg --files -g "!**/bin/**" -g "!**/obj/**" -g "!**/.scratch/**" -g "!**/.orchestrator-prototype/**"` returned the app source map; focused `rg -n "source-survey|work-summary|simple-hosted-dashboard|dashboard" Cli Dashboard Program.cs` confirmed CLI routing, hosted dashboard metadata, and `/api/source-survey`.
- Verification: `dotnet build Mcg.AgentOrchestrator.App.csproj --no-restore` exited `0`; output reported `Build succeeded`, `0 Warning(s)`, and `0 Error(s)`.
- Verification: `dotnet test ..\..\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-restore --filter 'SourceSurveyTests|DashboardHostTests' --results-directory .\TestResults --logger 'trx;LogFileName=simple-hosted-dashboard-task-4be2c032.trx'` exited `0`; TRX `TestResults\simple-hosted-dashboard-task-4be2c032.trx` records `total="4"`, `executed="4"`, `passed="4"`, and `failed="0"`.
- Hosted smoke: `dotnet run --project Mcg.AgentOrchestrator.App.csproj --no-build -- simple-hosted-dashboard http://localhost:5099/ --refresh 5 --no-open` served `/api/system/dashboard-host` and `/api/source-survey` on `127.0.0.1:5099`. The host metadata returned `CommandName=simple-hosted-dashboard`, `OperatorControlsEnabled=false`, `DashboardUrl=http://localhost:5099/dashboard`, and hosted dashboard URLs for `192.168.1.214` and `seventhson`; `/api/source-survey` returned `ReturnedFiles=85` and `TotalMatchedFiles=85`.
- Blockers and direct interventions: helper scripts under `.\scripts\` are not present in this app-root sandbox, `Start-Process` launch was rejected by sandbox policy, and the first smoke attempt on `5087` hit an address-in-use bind error. Endpoint verification succeeded on alternate port `5099`; later `netstat -ano | findstr "5087 5099"` showed no `LISTENING` socket on either smoke port. `dotnet run --project Mcg.AgentOrchestrator.App.csproj --no-build -- task 4be2c0329c064a60a19d72e753500af1` and the matching `progress ... completed` command both reported `Task '4be2c0329c064a60a19d72e753500af1' was not found`, so completion could not be posted through local app state.

## 2026-06-09 - Simple hosted dashboard goal task 476a3531a1e04efab43394e0f52f5f84

- Goal/task: delegated Developer task `476a3531a1e04efab43394e0f52f5f84`, objective `Simple hosted dashboard goal`.
- Changed files: `DOGFOOD_LOG.md` only for this evidence entry. No product source edits were required because the simple hosted dashboard command and read-only API guards are already implemented.
- Source inspection: `rg --files -g "!**/bin/**" -g "!**/obj/**" -g "!**/.scratch/**" -g "!**/.orchestrator-prototype/**"` returned the app source map; focused reads of `Program.cs`, `Dashboard\Hosting\DashboardHost.cs`, `Dashboard\Api\DashboardEndpoints.cs`, and `Dashboard\Rendering\DashboardRenderer.OperatorShell.cs` confirmed `simple-hosted-dashboard` routing, hosted bind URL generation, read-only operator controls, `/api/source-survey`, and hosted URL display.
- Verification: `dotnet build --no-restore` exited `0`; output reported `Build succeeded`, `0 Warning(s)`, and `0 Error(s)`.
- Hosted smoke: launched `dotnet bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll simple-hosted-dashboard 5200 --no-open --refresh 5` as a bounded background process. `curl.exe` confirmed `/health` returned HTTP `200`, `/api/system/dashboard-host` returned `CommandName=simple-hosted-dashboard`, `BindUrl=http://0.0.0.0:5200/`, `OperatorControlsEnabled=false`, `DashboardUrl=http://127.0.0.1:5200/dashboard`, and hosted dashboard URLs for `192.168.1.214` and `seventhson`. Mutating `POST /api/goals` returned HTTP `403` with `dashboard read-only: simple hosted dashboard does not allow operator actions`.
- Blockers and direct interventions: no product blocker remains. Two early PowerShell `Invoke-WebRequest` readiness probes timed out because the script compared response objects/body too strictly even while server logs showed `/health` 200 responses; the final `curl.exe` smoke supplied the verification evidence. The exact smoke PID was stopped, and `Get-NetTCPConnection -LocalPort 5200` returned no listener afterward.


## 2026-06-10 - Local qwen cap-validation report

Goal ID: `aa6e8301137a4378ba63e5fe45ac9f11` (CLI workspace), task 2 `093e044c`, Reviewer, live Ollama API run via `run 2`.

- Operator evidence inventory: prototype workspace has 541 goals, 0 recorded API executions, 0 stop-reason or token-usage records; all 20 `Model fit:` matches are template text inside dispatch prompts; 4 `truncated` matches are prompt-budget trims. The 768 routine paid output cap has never been exercised.
- Outcome: qwen3:8b produced a usable report (keep the 768 cap, collect execution data first). First real execution record in the system: 595 in / 851 out, stop reason `stop`, max 2048. Note: 851 output tokens exceeds 768, so this Simple report task would have been truncated on a routine paid model.
- Friction: provider registry probes `localhost` with a 2s timeout; localhost resolved through IPv6 fallback at ~2120ms vs 61ms via `127.0.0.1`, so the CLI silently used the offline scripted adapter. Workaround: `OLLAMA_BASE_URL=http://127.0.0.1:11434`. Product fix: probe 127.0.0.1 by default or lengthen/retry the probe, and surface scripted-adapter fallback loudly.
- Friction: the model echoed the fit template literally (`adequate|overkill|underpowered`); the hardened parser drops the echo instead of recording unknown fit. The operator's first manual note embedded `Model fit:` mid-line and was unparseable; a line-leading note was recorded. Both argue for a structured fit field.
- Verification: `verify-manual 2 passed` with line-leading note. Placeholder task 1 cancelled.
- Model fit: Ollama/qwen3:8b - adequate - bounded report from provided data - usable conclusion on first live attempt, minor structure drift.

## 2026-06-10 - Local agentic bridge validation (codex --oss, qwen-code)

Goal ID: `ea57a8e7b4114e8ca6e6431d50e1c6d3` (CLI workspace), task 2 `d2f0286e`, profile-dispatch through goal worktree.

- Machinery outcome: full pass. `workspace create` made the worktree, `profile-dispatch` expanded the qwen-code template with per-invocation env vars, `start-dispatch` ran qwen in `.orchestrator-worktrees/ea57a8e7`, logs/exit/refresh all recorded. Phase-1 worktrees and phase-2 launcher compose correctly.
- Model outcome: fail. qwen3:8b returned an empty response to the 1,326-char task brief. Across nine smokes: codex `--oss` + qwen emits tool calls as fenced JSON prose (harmony format mismatch; qwen2.5-coder and qwen3:8b) or thinks endlessly (qwen3:14b, 20+ min at 100% GPU); codex 0.137 removed `wire_api = "chat"`; Ollama serves `/v1/responses` but tool definitions do not reach qwen templates through codex. qwen-code + qwen3:8b makes real structured tool calls (validated twice) but botched a parameter every attempt (wrong name, then `C:\` root path) and gives up in `-p` mode.
- Model fit: Ollama/qwen3:8b - underpowered - agentic file write via qwen-code - structured calls work but parameters wrong every attempt; empty response under dispatch.
- Friction: dispatch process exit 0 with empty stdout auto-recorded as passing verification and completed the task; "completed" was indistinguishable from "did nothing". Verification-from-exit-code should require non-empty output or task-relevant evidence for Developer tasks.
- Friction: nvm shims (`codex.ps1`, `qwen.ps1`) hang on open redirected stdin (`Reading additional input from stdin...`); launching with `< NUL` via cmd avoids it. The orchestrator dispatcher inherits console stdin and did not hang.
- Next lever: `ollama pull gpt-oss:20b` for the codex `--oss` designed pairing (offload penalty on 10GB VRAM), or revisit when stronger small tool-calling models land. The `qwen-code-cli` profile is wired as the Ollama default either way.

## 2026-06-10 - Local bridge validated: qwen-code + reasoning disabled

Goal ID: `c2d382096b2245e19a096c9065e2a08d`, task 2 `c471c65f`, profile-dispatch in goal worktree. Corrects the previous entry`s "model underpowered" verdict.

- Root cause of all prior failures: Ollama`s qwen3 template ignores the `/no_think` soft switch and only honors the API think parameter; on the `/v1` endpoint that is `reasoning_effort` ("none" works, `think: false` is ignored). qwen-code never sent it, so every run paid full thinking overhead, and thinking-polluted turns also botched tool parameters.
- Fix: `.qwen/settings.json` `modelProviders` entry with `generationConfig.reasoning: false` for the model id + baseUrl. qwen-code honors it for env-selected models. Smoke went from 8+ min timeouts to 18-24 s. Direct API check: qwen3:14b with think off makes a perfect structured `write_file` call in 12.6 s at 6.5 tok/s (partial offload).
- Second requirement: qwen-code`s write tool demands absolute paths and qwen3:8b does not self-correct relative-path rejections in `-p` mode; task briefs must state the absolute target path. (Operator note: an earlier 14b run also competed with a game for the GPU; uncontended it is still offload-slow on 10 GB.)
- End-to-end result: dispatch ran qwen3:8b in `.orchestrator-worktrees/c2d38209`, BRIDGE.md created in 19 s with minor content drift (trailing period). Dirty-workspace guard correctly refused `workspace remove` until forced.
- Model fit: Ollama/qwen3:8b - adequate - agentic file write via qwen-code with reasoning disabled - 19s completion, minor content drift.
- Follow-up: include the dispatch working directory (absolute) in task briefs; commit `.qwen/settings.json` so worktrees inherit it (worktrees only carry committed files).

## 2026-06-10 - Claude bridge validated end-to-end (claude-cli, bypassPermissions)

Goal ID: `9630c178515141909ef19eee0d8c8c46` (CLI workspace), task 1 `844dc4ff`, Developer, subscription-dispatch in goal worktree.

- Pre-flight (direct CLI, outside orchestrator): the shipped default alias `claude-sonnet` is rejected by claude CLI (exit 1) - the claude-cli path could never have dispatched. Without a permission flag, `claude -p` denies the file write, replies BLOCKED, and exits 0 - the exit-code auto-pass would have completed the task with nothing done (same trap as the qwen empty-output case, but with exit 0 AND non-empty output). `--permission-mode acceptEdits` enables file edits; `bypassPermissions` also enables shell commands (parity with codex `workspace-write`).
- Fixes shipped: default `claude-cli` template now pins `--permission-mode bypassPermissions`; profile repair upgrades stale saved templates; claude-specific patch-capability check blocks Developer dispatch through permission-less claude templates; Anthropic default subscription alias is now null (CLI uses the agent's API model name - Anthropic API ids are valid CLI names); stale `claude-sonnet-4-20250514`/`claude-opus-4-20250514` defaults replaced with `claude-sonnet-4-6`/`claude-haiku-4-5`/`claude-opus-4-8` across provider defaults, dashboard options, and README.
- End-to-end result: `agent developer Anthropic claude-haiku-4-5` -> `simple-goal` -> `workspace create` -> `subscription-dispatch 1` -> `start-dispatch 1`. Dispatch ran claude in `.orchestrator-worktrees/9630c178`, exit 0 in 118 s; CLAUDE-BRIDGE.md created with exact requested content; worker self-verified via Get-Content and reported changed files. Dirty-workspace guard refused `workspace remove` until forced, as designed.
- Friction: the worker emitted its fit note as `**Model fit:** Anthropic/claude-haiku-4-5 — adequate — ...` (markdown bold + em dashes), invisible to the line-based parser. Fixed this session: parser now strips markdown decoration and normalizes en/em dashes; test covers the exact emitted line. Structured fit field remains the real fix.
- Friction: claude warns "no stdin data received in 3s" when stdin is redirected; the dispatcher inherits console stdin so it only delays, never hangs.
- Model fit: Anthropic/claude-haiku-4-5 - adequate - agentic file write via claude-cli - exact content, self-verified, ~2 min.
- Follow-up: Developer role in the CLI workspace is now `anthropic-developer` (Anthropic/claude-sonnet-4-6, subscription claude-cli); delete `src\Mcg.AgentOrchestrator.App\.orchestrator\agents.json` to restore the OpenAI default catalog.

## 2026-06-10 - First self-improvement loop: three backlog items via claude-cli workers

Goals `f55dc813`, `b469cc67`, `9d4e59d5` (CLI workspace), one Developer task each, claude-sonnet-4-6 via subscription-dispatch in goal worktrees. The orchestrator implemented its own backlog: A) exit-0/no-output dispatches now fail instead of completing (kernel guard + 2 tests), B) ScriptedModelProvider throws a configuration error instead of impersonating HUMAN_INPUT (worker correctly found and updated 2 pre-existing tests pinning old behavior), C) task briefs now state the dispatch working directory (parameter threading Core->Infrastructure + test).

- Cycle mechanics: simple-goal -> workspace create -> subscription-dispatch -> start-dispatch (orchestrator cost-guard flags as the explicit confirmations) -> Wait-Process watcher -> refresh-dispatch -> operator diff review + independent dotnet test in the worktree -> acceptance -> git merge -> workspace remove. Worker wall times: 3, 6, and 5.5 minutes. All three merged; main green at 358 tests.
- Friction: dispatch-family CLI commands only target the latest goal, forcing strictly sequential goals (new backlog entry). Acceptance never fast-forwards once main advances mid-goal (operator commits between fork and merge); printed merge command works but the auto-ff policy rarely applies in practice.
- Friction: complexity estimator classified all three "Implement the BACKLOG.md item..." briefs as Complex, tripping the large-paid-prompt guard at ~4k chars (threshold reads 12000 chars or 3 tasks, but complex-model selection alone requires the flag); harmless with the flag, noisy for a loop.
- Model fit: Anthropic/claude-sonnet-4-6 - adequate - scoped backlog items with file pointers and done-conditions - 3/3 first-attempt passes, honest test updates, no scope creep.

## 2026-06-10 - Context-window forensics correct the qwen verdicts again

Follow-up to the bridge validation after suspecting config interference in the 14b runs. Three real disruptions found:

- Ollama runs models at num_ctx 4096 by default (confirmed via /api/ps during a live run); qwen-code`s system prompt far exceeds that, so every qwen-code request had a truncated prompt. The 8b "successes" worked BECAUSE of truncation: with proper 16k/8k context variants (reasoning still disabled, settings verified in place), qwen3:8b returns an empty stream every time. The full qwen-code system prompt overwhelms it; the 4k-truncated view is accidentally load-bearing. Trailing-period drift occurred under truncation.
- qwen-code aborts requests at ~483s and earlier surfaced no error mid-run, so 14b looked "hung" when it was generating; with 16k context and thinking disabled it still cannot finish one turn inside 483s on the RTX 3080 (partial offload, ~6.5 tok/s). 14b in the qwen-code loop is compute-bound, conclusively. 14b remains excellent for direct single-shot API calls (perfect tool call, 12.6s).
- qwen-code rewrites the project .qwen/settings.json at process exit with its startup view; external edits between runs are lost (this wiped reasoning:false entries mid-investigation and produced confounded empty-response results). Sequential identical dispatches are safe because the rewrite matches the committed file; do not hand-edit the file while a qwen process is alive.

Working configuration stands as committed: base qwen3:8b (4k ctx) + reasoning:false + absolute paths; 3/3 file-write successes at ~20s. Fragile by construction - revisit when a small model handles the full prompt. Experimental Ollama variants qwen3-14b-16k, qwen3-8b-16k, qwen3-8b-8k remain installed (alias-only, shared blobs).

## 2026-06-11 - Loop iteration: provider env vars pinned in the e2e spawn helper

Goal `d89f3c6c` (CLI workspace), one Developer task, claude-sonnet-4-6 via subscription-dispatch in goal worktree. Fourth loop goal, same cycle as the first three; closes the "Pin provider env vars in the e2e spawn helper" backlog entry (commit d84f198).

- Outcome: `StartPrototypeDashboardProcess` now pins `OPENAI_API_KEY`/`ANTHROPIC_API_KEY`/`OPENAI_MODEL`/`ANTHROPIC_MODEL`/`OLLAMA_MODEL` to fixed sentinels alongside the existing `OLLAMA_BASE_URL` pin. Worker wall time 97 s. Operator verification matched the backlog's verify condition exactly: full `dotnet test` in the worktree with `OPENAI_API_KEY` set to a dummy value - 358 passed, 0 failed.
- Cycle note: acceptance auto-fast-forwarded main this time (no operator commits landed between fork and merge), confirming the auto-ff policy works when main is quiescent.
- Friction (repeat): complexity estimator again classified the "Implement the BACKLOG.md item..." brief as Complex, requiring `--confirm-large-paid-subscription-start` for a five-line test-helper edit. The worker's own fit note agrees the task was small. Second data point for tuning the estimator or keying it off brief size rather than wording.
- Friction (repeat): a VBCSCompiler left over from the worker's verification run held a lock on the worktree's Core obj dll; first operator `dotnet test` failed CS2012 until a second `dotnet build-server shutdown`.
- Model fit: Anthropic/claude-sonnet-4-6 - overkill - five-line test-helper edit with exact file pointer - first-attempt pass; worker self-assessed overkill. Candidate shape for haiku or local routing.

## 2026-06-11 - First multi-subtask loop goal: structured model-fit field landed in three sequential dispatches

Goal `33a2a799` (CLI workspace), three Developer tasks in one goal worktree, claude-sonnet-4-6 via subscription-dispatch. Closes the "Structured model-fit field" backlog entry (commits c7730b6, cf13925, 83e9ac1). Fit is now a first-class `ModelFitNote` field on `TaskVerificationRecord`/`TaskVerificationSnapshot`: extracted once at the `RecordVerification` chokepoint via `ModelFitEvidence.TryExtractNote`, preferred by `FindLatestNote`/`FindNotes`, with the line scrape kept as the fallback for pre-field snapshots (restore deliberately does not backfill).

- Multi-subtask mechanics validated: `simple-goal` for step 1 + `add-task Developer` for steps 2-3, each dispatched/reviewed sequentially in the same worktree (`subscription-dispatch N`/`start-dispatch N` address tasks by number fine; only cross-goal targeting is blocked). Briefs stated "already committed on this branch" context and each worker built on the prior commit without confusion - worker 2 used step 1's field, worker 3 tested against step 2's chokepoint behavior. Wall times 2.5/3.5/7.5 min; 363 tests green after each step (operator-verified independently in the worktree each time).
- Friction (recurring, now 3x in this goal alone): after a claude-cli worker runs `dotnet test` in the worktree, a VBCSCompiler instance survives and holds `obj/...Core.dll`, so the operator's first build fails CS2012; a second `dotnet build-server shutdown` + retry always clears it. Worth a small backlog item if it persists (e.g. workers run tests with `-p:UseSharedCompilation=false`).
- Friction (repeat): all three ~5k-char briefs classified Complex by the estimator, each requiring `--confirm-large-paid-subscription-start`.
- Model fit: Anthropic/claude-sonnet-4-6 - adequate - sequenced data-model/persistence/reader changes with tight briefs - 3/3 first-attempt passes; workers self-noted steps 1-2 were haiku-sized.
- Operator note: this goal pre-dated the planner/researcher/developer/tester pipeline question; per discussion with Miles the next loop item should run through the full five-role `goal` flow now that codex has reset.

## 2026-06-11 - First full five-role pipeline run: non-latest goal targeting (goal f2e3d68c)

Goal `f2e3d68c` (CLI workspace), the default Planner/Researcher/Developer/Tester/Reviewer pipeline via `goal`, payload "Dispatch commands cannot target a non-latest goal". Outcome: feature merged (commits 4710320, 8193d23, merge 441cd6d), 365/365 green, dispatch-family commands now accept a positional goal prefix or `--goal` flag, legacy `logs <task> <stream>` preserved. Roles: Planner codex/gpt-5.5 54s; Researcher codex/gpt-5.5 18.5min/172k tokens; Developer claude-sonnet-4-6 6.5min; Tester codex/gpt-5.5 2min; Reviewer Ollama/qwen3:8b 17s.

- Product defect: built-in codex subscription default `gpt-5.3-codex` is rejected by ChatGPT accounts (400, exit 1, recorded as TaskFailed - the exit-code guard from goal A worked). Smoke: `gpt-5.5` works, `gpt-5.5-codex` rejected. No CLI surface exists to set a subscription alias (`agent` reapplies the built-in default); operator hand-edited agents.json. Backlog entry filed.
- Product defect (the big one): dispatch briefs contain no prior-task evidence - `BuildTaskBrief` filters the timeline to the current task and reads only same-task execution/verification records, and the work-summary URL in the brief is unreachable for dispatched workers. Consequence observed live: the Planner's plan explicitly warned that `logs <task> <stream>` could be misparsed under a positional prefix; the plan-blind Researcher implemented exactly that bug 20 minutes later. Backlog entry filed.
- Role fidelity: the Researcher implemented the entire feature (handler rewrite + test + commit) instead of researching - with an "Implement..." objective, no plan visibility, and a writable worktree, role instructions did not contain it. It also expanded scope to the batch commands the Planner had deliberately deferred. The Tester, by contrast, stayed in role (ran focused + full suites, no changes, no commit).
- Operator-note handoff works: the regression was conveyed to the Developer via `progress`/`retry` timeline messages, which do land in the task brief; the Developer consumed the note and shipped the precise fix with a regression test. Friction: getting a note onto an undispatched task required a `progress running` -> `progress failed` -> `retry` dance because `progress` flips status and `subscription-dispatch`/`retry` both refuse Running tasks; a status-neutral `note` command would fix this.
- Reviewer on ApiOnly qwen reviewed the objective text, not the branch: no file access in API runs, so every claim about the code was stale; it echoed the fit template verbatim; HUMAN_INPUT round-trip (`input-needed`/`answer`) worked. Reviewer output was 988 tokens on a routine task - second data point above the 768 routine cap (first: 851).
- Acceptance could not fast-forward (operator backlog commits landed mid-goal); explicit `git merge` clean. Completed BACKLOG entry had to be removed by the operator - no role did it.
- Model fit: OpenAI/gpt-5.5 - adequate - planner/tester roles - sharp plan with falsifiable risks; disciplined tester. Researcher overran role, not model capability.
- Model fit: Ollama/qwen3:8b - underpowered - code review without file access - findings derived from objective text only.
- Verdict: pipeline mechanics (sequential role dispatch, mixed codex/claude/ollama workers, human-input round-trip) all function, but without cross-task evidence the roles are five disconnected workers and the pipeline's value over `simple-goal` + operator review is negative at ~5x the cost. The "dispatch briefs omit prior-task evidence" backlog item is the gating fix.

## 2026-06-11 - Pipeline handoff gap closed: briefs now carry prior-task evidence (goal a11b7b57)

Goal `a11b7b57` (CLI workspace), one Developer task, claude-sonnet-4-6, simple-goal cycle. Closes the gating backlog item from the pipeline run, same day it was filed (commit 72be355). `BuildTaskBrief` now emits a `## Prior Task Evidence` section: up to the 3 most recent completed earlier tasks in goal order, each as `### {Role}: {title}` plus latest verification stdout trimmed through the existing TrimEvidenceBlock budget; omitted for single-task goals. Two tests cover presence and omission. Worker wall time 5.5 min; 367/367 green, operator-verified independently.

- Next validation: rerun a five-role `goal` pipeline on a future backlog item and confirm the Researcher/Developer briefs actually carry the Planner's plan (and that role drift shrinks when they do).
- Model fit: Anthropic/claude-sonnet-4-6 - adequate - scoped feature in brief construction with explicit done-conditions - first-attempt pass, 6/6 in the loop so far.

## 2026-06-11 - Pipeline rerun with handoff live: codex default fixed, coordination confirmed (goal e9d05a26)

Goal `e9d05a26`, five-role pipeline, payload "Default codex subscription model is rejected by ChatGPT accounts". Merged (5b36d5d, merge after operator backlog commit), 369/369 green. Defaults and dashboard options now use `gpt-5.5` via a named `AgentCatalog.OpenAiSubscriptionModelAlias` constant; stale `gpt-5.3-codex` catalog aliases are repaired on load (narrow: OpenAI + codex-cli + exact stale alias; customs preserved). A/B against goal f2e3d68c, same roles and models, handoff fix (72be355) live:

- The Researcher's brief carried `## Prior Task Evidence` with the Planner's plan - first dispatched worker ever to receive cross-task evidence. It STILL implemented the whole feature (role overreach is driven by the "Implement..." objective + writable worktree, not missing context), but this time the implementation conformed to the plan exactly: same boundary, narrow repair semantics, all three dashboard default sources, tests pinning default and repair, backlog entry removed. Contrast: the plan-blind run introduced the plan's predicted bug; the plan-fed run shipped the plan.
- The Developer recognized from prior-task evidence that commit 5b36d5d already completed the work, verified 368/368, and changed nothing - "already done" handling now works.
- Reviewer (ApiOnly qwen) is unchanged: file-blind, objective-text review, template echo, HUMAN_INPUT for a fact sitting in the Researcher's verification record. Root cause: `AgentTaskRunner` builds API prompts separately and has no prior-task section - filed as new backlog entry (API-prompt parity). Third routine-task output above the 768 cap (1010 tokens; prior: 851, 988).
- New friction from the handoff fix itself: briefs grow as roles complete (4.2k chars by task 3, 5.0k by task 4), so every later dispatch trips the 4000-char large-paid-prompt guard even at complexity=Simple. The guard and the evidence section need reconciling (raise threshold, budget the section tighter, or exempt accumulated evidence).
- Wall times: Planner 46s, Researcher 17min/104k tokens, Developer 2.2min (verify-only), Tester 2.4min, Reviewer 18s.
- Model fit: OpenAI/gpt-5.5 - adequate - planner/researcher/tester roles on a scoped default-fix - plan was precise, implementation per plan, disciplined verification.
- Verdict: with prior-task evidence in briefs the pipeline coordinates instead of colliding. Remaining structural issues: role-boundary enforcement (Researcher implements), API-prompt parity, and the guard/brief-size interaction.

## 2026-06-11 - API-prompt parity: prior-task evidence extracted to a shared helper (goal d51ebbf6)

Goal `d51ebbf6` (CLI workspace), one Developer task, claude-sonnet-4-6, simple-goal cycle, closes the API-parity backlog item filed during the pipeline rerun (commit 76eb37c, fast-forward merge). `PromptContextFormatter.BuildPriorTaskEvidenceLines` now owns the selection/format; `BuildTaskBrief` and `AgentTaskRunner` both call it, so ApiOnly agents (the qwen Reviewer) finally see prior roles' verification stdout. End-to-end test asserts the outgoing provider prompt for task 2 contains task 1's verification stdout. 369/369 green, operator-verified independently. Worker wall time 8 min.

- Friction: `workspace remove` failed because a leaked process held dispatch log files under the worktree's `.orchestrator-prototype` test state (e2e tests spawn real processes); the removal half-completed - worktree unregistered but directory left behind, and a second `workspace remove` then said "no workspace to remove". Manual branch delete + directory removal (after the holder exited) finished the job. Worth hardening: removal should be atomic or resumable, and test-spawned processes should not outlive the suite.
- Model fit: Anthropic/claude-sonnet-4-6 - adequate - helper extraction + dual wiring + end-to-end test - first-attempt pass, 7/7 in the loop.

## 2026-06-11 - Role boundaries enforced: sandbox-by-role in dispatch commands (goal c5e626c4)

Goal `c5e626c4` (CLI workspace), one Developer task, claude-sonnet-4-6, simple-goal cycle. Direct response to the Researcher-implements drift observed in both pipeline runs; per operator/Miles decision the boundary is now mechanical, not advisory (commit on goal branch, fast-forward merge to 96b1322). 376/376 green, operator-verified independently.

- Design landed: codex-cli template uses `--sandbox {sandboxMode}` and claude-cli `--permission-mode {permissionMode}`; `WorkerProfileDispatcher.BuildDispatchVariables` resolves them by task role - Developer/Tester get workspace-write / bypassPermissions, Planner/Researcher/Reviewer get read-only / plan. Patch-capability validation accepts the placeholder forms; stale saved templates (old hardcoded flags) repair on load in both WorkerProfileStore and DemoWorkspaceSeeder - the worker found the seeder repair site without it being named in the brief. Role requirements now state the boundary in both full and compact prompt variants (read-only roles: do not modify repository files; Tester: build/run tests but no source changes).
- Worker wall time 22 min - the largest single-task brief of the loop (6.8k chars, 8 files, 7 new tests + 8 updated assertions), still a first-attempt pass; 8/8 for sonnet on scoped briefs.
- Friction (repeat): `workspace remove` half-failed again on e2e-test file locks (second occurrence; same manual branch-delete + directory-removal recovery). Now a clear hardening candidate.
- Validation pending: the next five-role pipeline run is the live test - a Researcher dispatch should carry `--sandbox read-only` and be unable to commit, making the Developer task the implementation point again.
- Model fit: Anthropic/claude-sonnet-4-6 - adequate - coordinated multi-file infrastructure change with explicit design in the brief - full scope including an unnamed repair site, no over-engineering.

## 2026-06-11 - Status-neutral notes shipped; live role sandbox validated (goal 9d78b9c1)

Goal `9d78b9c1`, five-role pipeline, closes `Status-neutral task note command` and the live role-sandbox validation backlog entries (commit 0f78b85, fast-forward merge). New `note <task-number> <message>` records `TaskNote` timeline events without changing task status; notes are decision-relevant for briefs and displayed in dashboard/transcripts. Tests cover kernel status preservation + brief inclusion and CLI note/retry/subscription-dispatch compatibility. Operator gate: reviewed diff, `git diff --check`, independent `dotnet test --no-restore --verbosity minimal` in the worktree exit 0.

- Live sandbox evidence: Planner and Researcher dispatches used codex `--sandbox read-only`; `git status --short` after Researcher was clean; Developer was the first implementation role and its retry dispatch used codex `--sandbox workspace-write`.
- Frictions: initial goal objective was truncated by PowerShell backtick escaping in a CLI argument; malformed worktree was removed before any paid worker started. Claude Developer failed before edits due session limit, so Developer was rerouted to OpenAI/codex and task assignment was repaired in orchestrator state. Codex Developer and Tester both printed final output / `tokens used` but wrapper processes stayed running with no exit file; exact `cancel-dispatch` + `verify-manual` repaired task evidence, and a backlog item was filed. `workspace remove` again unregistered the worktree but left `.orchestrator-worktrees/9d78b9c1` because a test-spawned process holds a prototype dispatch log; branch was deleted manually, orphan directory remains until the holder exits.
- Reviewer data point: Ollama/qwen3:8b ApiOnly output 1139 tokens, fourth routine-cap data point above 768 (prior 851, 988, 1010).
- Model fit: OpenAI/gpt-5.5 - adequate - Planner/Researcher/Tester and rerouted Developer; good coordination and implementation, but codex wrapper hang made evidence recording manual.
- Model fit: Anthropic/claude-sonnet-4-6 - blocked - Developer subscription dispatch failed immediately on account session limit before edits.
- Model fit: Ollama/qwen3:8b - adequate - evidence-only review without file access; identified the expected dashboard/worktree-state limitation but echoed the fit template style.

## 2026-06-11 - Codex dispatch wrapper hang handled (goal 7646c03c)

Goal `7646c03c`, simple-goal Developer task, rerouted to OpenAI/gpt-5.5 while Claude was session-limited. `BackgroundDispatchRunner` now completes from an existing exit file even when the wrapper process still appears live, and detects idle codex wrappers after final token output as failed with captured stdout/stderr evidence. Tests cover both paths; commit `e4f72b3` fast-forwarded to main. Operator gate: reviewed diff, `git diff --check`, independent `dotnet test --no-restore --verbosity minimal` in the worktree exit 0.

- Live smoke: follow-up no-edit goal `56b6f211` ran codex-cli in an isolated worktree; `refresh-dispatch 1` recorded Completed in 12 s, exit 0, `pid running=False`, no `cancel-dispatch`.
- Friction: the implementation dispatch itself was still supervised by the old runner and reproduced the old hang after full test output; exact `cancel-dispatch` + `verify-manual` repaired evidence. `workspace remove` again half-failed on a locked prototype log; exact stale PIDs 23692/46576/44464 were stopped before deleting the orphan worktree.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped infrastructure fix plus tests under Claude outage - first-attempt implementation and tests passed; wrapper supervision bug required operator smoke to validate after merge.
- Model fit: OpenAI/gpt-5.5 - adequate - no-edit smoke validation - produced concise completion evidence and exited cleanly.

## 2026-06-11 - Workspace remove resumes after partial cleanup (goal 2c8af0da)

Goal `2c8af0da`, simple-goal Developer task, OpenAI/gpt-5.5 while Claude was session-limited. `GoalWorktrees.Remove` now handles already-unregistered worktrees by deleting the leftover directory and `goal/<prefix>` branch, with retry/backoff around transient directory delete failures. Regression test simulates a pruned worktree registration with leftover directory/branch; commit `9c716c0` fast-forwarded to main. Operator gate: reviewed diff, `git diff --check`, independent `dotnet test .orchestrator-worktrees\2c8af0da --no-restore --verbosity minimal` passed after the known VBCSCompiler shutdown ritual.

- Live cleanup evidence: first `workspace remove` still failed while a prototype log was actively held, leaving the worktree unregistered with directory + branch remaining; a second `workspace remove` resumed successfully and removed both after the holder released.
- Friction: `refresh-dispatch` for the completed codex worker printed a huge stderr/prompt/diff payload (`[truncated 350284 chars]`), so a bounded-output backlog item was filed. The VBCSCompiler lock reproduced again with PID 41968.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped Git/worktree infrastructure fix plus regression test - handled the edge case correctly, though high reasoning produced verbose logs.

## 2026-06-11 - Worker dispatch shuts down build servers after exit (goal d47ec298)

Goal `d47ec298`, simple-goal Developer task, OpenAI/gpt-5.5 while Claude was session-limited. `BackgroundDispatchRunner` now wraps non-local dispatches in `try/finally` and runs best-effort `dotnet build-server shutdown` before writing the exit file; local dispatches can skip the cleanup. Tests cover wrapper cleanup and local skip, AGENTS.md documents the new operator expectation, and commit `e7f0a64` fast-forwarded to main. Operator gate: reviewed diff, `git diff --check`, focused `WorkerDispatchTests` passed 44/44, full `dotnet test .orchestrator-worktrees\d47ec298 --no-restore --verbosity minimal` exited 0.

- Live smoke: no-edit goal `a1f3368f` ran under the new wrapper. The worker's own `dotnet test --verbosity minimal` hit a pre-existing VBCSCompiler lock, but after wrapper exit the operator immediately ran `dotnet test .orchestrator-worktrees\a1f3368f --no-restore --verbosity minimal` with no manual shutdown and it exited 0.
- Frictions: `refresh-dispatch` repeatedly threw on locked live stdout files during d47ec298, requiring exact `cancel-dispatch` + `verify-manual` despite a committed branch and passing independent tests; backlog now tracks locked-log refresh handling. Miles also reported repeated Windows Firewall prompts for `Mcg.AgentOrchestrator.App`, now tracked as a loop blocker.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped dispatch-wrapper cleanup plus tests - low reasoning produced a first-attempt commit; live smoke showed the fix covers post-worker operator tests but not stale locks that exist before the worker command starts.
- Model fit: OpenAI/gpt-5.5 - overkill - no-edit smoke validation - simple command execution/reporting was useful as evidence but did not need a paid large model.

## 2026-06-11 - Paid subscription prompt guard retuned (goal bc128354)

Goal `bc128354`, simple-goal Developer task, OpenAI/gpt-5.5 after Miles chose Option 5. Subscription guard now uses 6000 simple / 9000 complex / 18000 batch thresholds, applies a 2000-char prior-task-evidence allowance, and no longer requires `--confirm-large-paid-subscription-start` solely because a task is Complex or uses a complex paid model. API prompt thresholds remain 4000/6000. Tests cover under-threshold complex-model starts, inherited-evidence allowance, oversized prompts, and batch/fanout risk; commit `3d978a1` merged to main.

- Operator gate: reviewed diff, `git diff --check main..goal/bc128354` passed, independent `dotnet test .orchestrator-worktrees\bc128354 --no-restore --verbosity minimal` exited 0. `start-dispatch` for this pre-fix run required the large flag solely for complex model use, confirming the before-state.
- Frictions: root `mcg-orchestrator.cmd` was blocked by unrelated dirty source changes causing duplicate switch-label build errors, so active-state commands used `dotnet run --no-build`. `refresh-dispatch` hit the known locked stdout issue and required `cancel-dispatch` + `verify-manual`. Manual merge used `git merge --autostash` to preserve unrelated local edits; uncommitted post-acceptance leftovers in the goal worktree were preserved in stash `preserve bc128354 post-acceptance leftovers` before cleanup.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped cost-policy change with broad guard/dashboard/CLI tests - first-attempt implementation passed, but high reasoning produced long logs and stale codex wrapper cleanup work.

## 2026-06-11 - Developer/Tester dispatch completion now requires file-change evidence (goal feadc825)

Goal `feadc825`, simple-goal Developer task, rerouted to OpenAI/gpt-5.5 because Claude was session-limited. Closes the false-completion gap from goal `03192a9b`: non-local Developer/Tester dispatches in `goal/<prefix>` worktrees now fail successful exits unless they have commits after dispatch start or an explicit no-change rationale with a clean worktree. Failure evidence includes branch, head, worktree status, and commit count. Commit `e8618a4` fast-forwarded to main.

- Operator gate: reviewed diff limited to `BackgroundDispatchRunner` and `WorkerDispatchTests`; independent `dotnet test .orchestrator-worktrees\feadc825 --verbosity minimal` exited 0. Worker also reported full `dotnet test --verbosity minimal` pass.
- Frictions: `profile-dispatch 1 codex-cli` on an Anthropic-assigned task recorded a Running dispatch with unresolved `{subscriptionModelName}` placeholders and no process; recovered via `progress failed` + `retry` and filed a backlog item. Manual `worker-dispatch` needed a literal worktree `--cd` because `{workingDirectory}` was not resolved in that path. The codex wrapper printed final output and committed but did not exit, so exact `cancel-dispatch` + `verify-manual` closed the task. `workspace remove` unregistered the worktree and branch cleanup succeeded, but `.orchestrator-worktrees/feadc825` remains locked after build-server shutdown, exact post-test dotnet PID stop, and long-path removal; filed a cleanup diagnostics backlog item.
- Model fit: OpenAI/gpt-5.5 - adequate - scoped C# dispatch guard plus focused tests; manual reroute worked during Claude outage, but profile/worker dispatch escape hatches need hardening.
- Model fit: Anthropic/claude-sonnet-4-6 - blocked - session-limit window produced empty logs until cancelled.

## 2026-06-11 - Usage-limit review acknowledgement and retry-after deferral shipped (goal 8db3d426)

Goal `8db3d426`, simple-goal Developer task, OpenAI/gpt-5.5 via normal `codex-cli`. Closes the repeated subscription usage-limit dead-end: `subscription-dispatch` now supports `--confirm-limit-review <note>`, dashboard/API task dispatch accepts `confirmLimitReview=true` plus `note`, review evidence is persisted, and future `SubscriptionRetryAfter` windows block `start-dispatch`/local execution before burning another attempt. Commit `72e80ef` fast-forwarded to main.

- Operator gate: reviewed core review-state semantics, CLI parsing/guarding, dashboard/API parity, start deferral, and focused tests; independent `dotnet test .orchestrator-worktrees\8db3d426 --verbosity minimal` exited 0.
- Frictions: Codex wrapper again printed final output and committed but did not exit, requiring exact `cancel-dispatch` + `verify-manual`. The worktree had unrelated tracked source modifications (`rollback-state`/state-store changes) after the worker commit, so `workspace remove` refused and later unregistered the worktree but left `.orchestrator-worktrees/8db3d426` locked; filed backlog items for dirty post-commit worker leftovers and the still-live Codex wrapper issue.
- Model fit: OpenAI/gpt-5.5 - adequate - cross-layer CLI/core/dashboard implementation with tests; high reasoning recovered from compile/test issues but produced long logs and wrapper cleanup friction.

## 2026-06-11 - Unresolved dispatch template variables rejected before state mutation (goal 74686f04)

Goal `74686f04`, simple-goal Developer task, OpenAI/gpt-5.5 via `codex-cli`. `WorkerCommandTemplate.Prepare` now rejects leftover `{Identifier}` template variables before prompt-file creation, dispatch recording, or task status mutation. Manual `worker-dispatch` now builds the task brief with the goal execution directory and supplies generic dispatch variables; profile mismatches such as Anthropic-assigned task + `codex-cli` fail before mutating state. Worker commit was amended to `95e4ee7` to remove an unintended `BACKLOG.md` edit before acceptance.

- Operator gate: reviewed diff limited to `CliCommandHandlers.Workers`, `WorkerCommandTemplate`, `WorkerProfileDispatcher`, and `WorkerDispatchTests`. Because the original worker worktree had unrelated dirty source edits after commit, verification used a clean detached worktree at `95e4ee7`; `dotnet test .orchestrator-worktrees\verify-74686f04 --verbosity minimal` exited 0 and `git status --short` stayed clean. Commit fast-forwarded to main.
- Frictions: Codex wrapper again required exact cancellation after final output. The original goal worktree and the detached verification worktree both unregistered but remained locked on directory deletion. Dirty post-commit worker leftovers refine the previous completion guard: file-touching dispatches should require a clean worktree even when they made commits.
- Model fit: OpenAI/gpt-5.5 - adequate - focused dispatch-template validation plus tests; high reasoning was more than needed but recovered from an initial compile issue.

## 2026-06-11 - File-touching dispatches must leave clean worktrees (goal 6a35eb1a)

Goal `6a35eb1a`, simple-goal Developer task, OpenAI/gpt-5.5 via `codex-cli`. Closes the post-commit dirty-worktree gap from goals `8db3d426` and `74686f04`: non-local Developer/Tester dispatches in goal worktrees now fail if `git status --short` is dirty at completion, even when commits exist or the worker supplied a no-change rationale. Failure evidence includes the short status preview. Commit `a92c130` fast-forwarded to main.

- Operator gate: reviewed diff limited to `BackgroundDispatchRunner` and `WorkerDispatchTests`; independent `dotnet test .orchestrator-worktrees\6a35eb1a --verbosity minimal` passed after `dotnet build-server shutdown` cleared a VBCSCompiler lock. Worker reported focused `WorkerDispatchTests` 63/63 and full `dotnet test` exit 0.
- Frictions: root `mcg-orchestrator.cmd` rebuild is now blocked by unrelated dirty `state-rollback` source edits, so final state commands used `dotnet run --no-build --project ...`. Directly running the app exe once loaded an older legacy state location and recorded an irrelevant manual verification there; avoid that path. Codex wrapper again required exact cancellation after final output.
- Model fit: OpenAI/gpt-5.5 - adequate - focused dispatch guard plus regression tests; completed the scoped change, but Codex wrapper exit behavior remains the dominant operator cost.
