# Dogfood Log

Entry convention: keep entries short and record only durable product signal. For subscription/API-authored work, add `Model fit: <model or launcher> - adequate|overkill|underpowered - <task shape> - <short reason>`.

Older entries are rotated to `docs/DOGFOOD_LOG-2026-06.md`. When this file grows past roughly 500 lines, move all but the most recent entries to a dated archive under `docs/`.

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
