# Dogfood Log

## 2026-06-04 - Prototype creation auto-handoff starts work

Goal ID: `cb69e396907d4735a8d575a4765b7cc5`

Validation Goal ID: `ad5d5a68da214387bb5b2dfa2c8bf23d`

Objective: Finish reorienting demo remnants to prototype, add creation-time automatic handoff configuration in the dashboard, preserve auto-handoff as the default, and validate with focused tests.

What worked:
- Created the goal through the prototype dashboard form on `http://localhost:5087/`.
- The dashboard creation path defaulted to auto-handoff and delegated the first Planner task.
- Ran the Planner subscription task through the dashboard; it completed and produced a concrete file-level plan.
- Added a follow-up Developer task through the dashboard add-task form after the first Developer task stalled.
- Exercised the dashboard Run and Start controls for both Developer attempts, proving the current product still required a second click before this fix.
- Created validation goal `ad5d5a68da214387bb5b2dfa2c8bf23d` through the dashboard form after the fix; it recorded `TaskDispatchRecorded` and `TaskProcessStarted` for task 1 without a Start click.
- Added creation-form `autoHandoff` control, default checked, with form serialization that sends `true` when checked and `false` when unchecked.
- Changed creation auto-handoff to use the bounded subscription-until-blocked workflow so default goal creation starts subscription work automatically instead of only recording a dispatch.
- Preserved `autoHandoff:false` as manual mode: the goal is created and delegated, but no dispatch or process is recorded.

What blocked progress:
- Two subscription-backed Developer attempts stalled in broad repository/context reads and did not complete the implementation.
- The dashboard Run action still only prepared dispatch before the fix; the operator had to click Start prepared work separately.
- Broad `rg` from agents included generated `bin`/`obj` files and created noisy logs.
- The live dashboard locked `Mcg.AgentOrchestrator.App.dll` during verification, requiring a dashboard stop before build/test.

Direct intervention:
- Cancelled the stuck Developer task processes through the orchestrator API.
- Made a scoped source patch after the orchestrator blocked itself: lifecycle auto-handoff now advances subscription work until blocked, the create-form label says it automatically starts handoff, and focused tests assert default process evidence plus manual no-dispatch/no-process behavior.
- Stopped dashboard PID `56892` to release the Windows apphost lock, then restarted `prototype-ui` on port `5087`.

UI/API friction:
- The dashboard exposed Start prepared work, but did not automatically start prepared handoff from goal creation before this fix.
- Cancel was available through the task API but not easy to reach from the current next-action path.
- Headless dashboard browser automation repeatedly needed escalation because of Windows sandbox spawn failures.
- The dashboard create script could submit the form, but there is no goal-detail GET endpoint; transcript and rendered page inspection were needed.
- Broad task queries can follow the current monitor goal rather than the explicitly desired goal, making goal-specific inspection awkward.

Verification:
- Focused tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AdvanceLoopTests|FullyQualifiedName~DashboardRenderingTests|FullyQualifiedName~DashboardHostTests"` passed 13/13.
- `dotnet build` passed: 0 warnings, 0 errors.
- `dotnet test` passed: Core 57/57 and Infrastructure 46/46.
- Restarted the prototype dashboard and recorded task 6 as completed with passed manual verification evidence.
- Live dashboard validation goal `ad5d5a68da214387bb5b2dfa2c8bf23d` showed `Last process: pid=60092 running=True` immediately after creation; the throwaway validation process was then cancelled.

Next follow-up:
- Exclude generated `bin`/`obj` paths from default role prompts or task briefs so subscription agents stop wasting context on build artifacts.
- Add in-dashboard cancel/recover controls for stuck running subscription processes.
- Add a goal-detail JSON endpoint or make goal-specific task inspection first-class in the dashboard.
- Continue the critical review of Researcher, Tester, and Reviewer role performance; Researcher still defaults to Claude CLI and was not exercised here per the no-Claude-concerns constraint.

## 2026-06-04 - Prototype subscription execution by default

Goal ID: `1aea2a09812948d0abb145b9a3ab47d6`

Validation Goal ID: `67018b8ffa6548f08690d251a3281984`

Execution-root Validation Goal ID: `847f5663f9534fd3a589578f3541dee6`

Objective: Make the persisted prototype worker profiles execute real subscription agents by default instead of echoing prompt paths. Keep `local-echo` as the explicit harmless echo profile, but make `codex-cli` and `claude-cli` real execution profiles.

What worked:
- Created the implementation goal through the dashboard form.
- Confirmed the persisted prototype worker catalog initially still had `codex-cli` and `claude-cli` as `Write-Output {promptPath}`.
- Changed prototype seeding and repair so persisted legacy echo subscription profiles are migrated to real defaults while `local-echo` stays explicit.
- Updated `codex-cli` to run implementation-capable Codex: `codex exec --skip-git-repo-check --sandbox workspace-write --cd {workingDirectory} ...`.
- Added `{workingDirectory}` template substitution during dispatch preparation.
- Restarted the dashboard and confirmed `/api/worker-profiles` showed real `codex`, real `claude`, and explicit `local-echo`.
- Created validation goal `67018b8ffa6548f08690d251a3281984` through the dashboard and clicked `Continue subscription handoff` in the dashboard.
- The validation task launched actual Codex, completed with exit code `0`, recorded passed verification, and the validation goal reached `Completed` / `IsAccepted: true`.
- Separated prototype state storage from task execution: state remains isolated under `.orchestrator-demo`, while `mcg-orchestrator.cmd` exports the repository root as the dashboard execution directory.
- Created execution-root validation goal `847f5663f9534fd3a589578f3541dee6` through the dashboard and clicked `Continue subscription handoff`.
- The execution-root validation dispatch recorded `--cd 'C:\Users\miles\vcs\mcg-agent-orchestrator\'`, and the nested Codex log reported `workdir: C:\Users\miles\vcs\mcg-agent-orchestrator` with `sandbox: workspace-write`.

What blocked progress:
- The first real Codex launch proved the profile was no longer fake, but it exposed a second defect: Codex defaulted to `sandbox: read-only`, which would block Developer tasks from making source edits.
- The obsolete read-only Planner run was cancelled through the orchestrator after its evidence was inspected.
- The first workspace-write validation used the isolated prototype state directory as the editable root. That was corrected by adding a separate execution directory to `OrchestratorWorkspace`.
- The execution-root validation task was cancelled after launch evidence was captured because its broad repository scan was not needed to prove the dispatch command.

Direct intervention:
- Updated `DemoWorkspaceSeeder` because the existing persisted worker profile could not execute implementation work.
- Updated default worker profiles and dispatch template variables to support workspace-write Codex execution with an explicit working directory.
- Added `MCG_ORCHESTRATOR_REPOSITORY_ROOT` to `mcg-orchestrator.cmd` and taught the hosted dashboard to use it as the task execution directory.
- Updated README wording from demo echo-profile behavior to persistent prototype subscription execution behavior.

UI/API friction:
- Dashboard browser automation was still required in this headless session to prove goal creation and subscription handoff button clicks.
- The dashboard exposes the final command after dispatch, but it does not warn ahead of time when a subscription profile is real-but-read-only.
- The standard command is still named `demo-ui`, even though the runtime behavior is now a persisted prototype workspace with real subscription profiles.

Verification:
- Focused infrastructure tests passed: 6/6 for seeder migration, worker profile defaults, dispatch working-directory substitution, and dashboard execution-directory dispatch.
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed: 0 warnings, 0 errors.
- `dotnet test Mcg.AgentOrchestrator.sln --no-restore` passed: Core 57/57 and Infrastructure 45/45.
- Dashboard validation goal `67018b8ffa6548f08690d251a3281984` passed with one subscription-backed Codex dispatch, exit code `0`, one passed verification, and no pending human input.
- Dashboard validation goal `847f5663f9534fd3a589578f3541dee6` recorded a subscription-backed Codex dispatch from the repository root with workspace-write enabled; it was cancelled after command/log evidence was captured.

Next follow-up:
- Rename or alias `demo-ui` toward prototype terminology without breaking the existing dogfood command.
- Surface the active workspace path and effective subscription command before dispatch, including sandbox mode and working directory.

## 2026-06-04 - Role quality and subscription reviewer tuning

Goal ID: `2354b94a689b4123b45b732b650f8ba4`

Objective: Dogfood improvement: tighten SDLC role quality after skeptical review. Use `C:\Users\miles\vcs\mcg-agent-orchestrator` as the repository. Improve planner, researcher, tester, and reviewer task prompts so each role produces concrete evidence instead of generic summaries. Bias reviewer execution toward the Codex subscription profile. Add focused tests for role prompt content, automatic handoff loop behavior, and persistent demo workspace hardening.

What worked:
- Created the improvement goal through the dashboard on `http://localhost:5087/`.
- Confirmed the goal persisted in the demo workspace and exposed assigned tasks, next actions, and empty evidence surfaces after dashboard restart.
- Added shared role-specific SDLC prompt requirements for generated task briefs and API-backed model execution prompts.
- Strengthened the default SDLC verification plans so Planner, Researcher, Developer, Tester, and Reviewer tasks have concrete proof expectations.
- Changed the default Reviewer catalog entry to prefer the OpenAI/Codex subscription profile instead of the Anthropic CLI profile.
- Updated the default `codex-cli` worker profile to include `--skip-git-repo-check`, matching the non-git prompt workspace used by subscription dispatch.
- Hardened persistent demo seeding so missing `agents.json` or `workers.json` are repaired without overwriting existing `state.json`.
- Added focused coverage for role prompt content, model prompt routing, reviewer provider selection, default catalog configuration, worker profile defaults, persistence repair, and the automatic handoff loop stopping at manual verification.

Direct intervention:
- Source edits were required because the currently persisted demo `codex-cli` worker profile still uses a safe `Write-Output` template from earlier demo seeding. Running the dashboard tasks would only echo prompt paths rather than implement changes.
- Added `InternalsVisibleTo` for the infrastructure test assembly so the dashboard command service advance loop can be tested without broadening the public API.

UI/API friction:
- The dashboard does not make the active worker command/template obvious during the goal flow, so it is easy to miss that persisted demo profiles are still safe echo profiles.
- Updating the default worker catalog does not automatically migrate existing persisted demo worker profiles.
- The dashboard still does not show the active workspace path in-page.
- Continuation loop results are available through API DTOs/tests but are not summarized prominently in the dashboard operation status.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed: 0 warnings, 0 errors.
- Focused core tests for role briefs and model execution passed: 4/4.
- Focused infrastructure tests for catalog defaults, worker profile defaults, persistence repair, and advance loop behavior passed: 4/4.
- `dotnet test Mcg.AgentOrchestrator.sln --no-restore` passed: Core 57/57 and Infrastructure 42/42.
- Restarted the dashboard on `http://localhost:5087/` and confirmed `/health`, `/api/goals`, `/api/next`, and `/api/evidence` for the dogfood goal were reachable.

Next follow-up:
- Add dashboard visibility for active workspace path and worker/profile command mode before running a task.
- Make prototype subscription execution real by default instead of requiring a safe-vs-real switch.
- Render the advance-loop result summary in the dashboard operation status.

Follow-up completed:
- `2026-06-04 - Prototype subscription execution by default` migrated persisted prototype `codex-cli` and `claude-cli` profiles from echo templates to real local CLI execution profiles.

## 2026-06-04 - Persistent demo dashboard workspace

Goal ID: `662cf3eae19a4faca36fffe72e61cc1f`

Objective: Make `demo-ui` persist by default without adding a flag, while keeping demo state isolated from the real `.orchestrator` workspace.

What worked:
- Created the persistence goal through the dashboard form.
- Changed `demo-ui` to use a stable workspace at `src/Mcg.AgentOrchestrator.App/.orchestrator-demo/workspace` under the app runtime working directory.
- The demo seeder now seeds sample state only when the persistent demo `state.json` is missing.
- Created marker goal `ade05f9cb7d2454182955cf41836a42c` through the dashboard: `Persistence marker: survives demo-ui restart 2026-06-04T04:15Z`.
- Restarted the standard dashboard command and verified the marker goal remained present through `/api/goals`.

Direct intervention:
- Updated `DemoWorkspaceSeeder` to use a deterministic workspace path and to avoid overwriting existing state.
- Updated dashboard startup messaging to say demo state is persistent and isolated.
- Made the seeder public so persistence behavior can be covered by focused tests.

UI/API friction:
- `dotnet run` starts the app with the app project as the runtime working directory, so the persistent demo workspace appears under `src/Mcg.AgentOrchestrator.App`, not the repository root used to launch `mcg-orchestrator.cmd`.
- The dashboard still does not display the workspace path in-page; confirming the exact persistence root required shell inspection.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed: 0 warnings, 0 errors.
- `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter FullyQualifiedName~DemoWorkspaceSeederReusesPersistentWorkspaceWithoutOverwritingState` passed: 1/1.
- `dotnet test Mcg.AgentOrchestrator.sln --no-restore` passed: Core 56/56 and Infrastructure 41/41.

Next follow-up:
- Surface the active workspace path in the dashboard header or setup panel so operators can tell where state, prompts, and logs are being persisted.

## 2026-06-04 - Automated task handoff without human input

Goal ID: `e0820db4e30c443bbc7f4dec695795a8`

Objective: Automate task handoff when no human input is needed by letting the dashboard continue executable next actions until blocked by human input, failed work, manual verification, a running process, or acceptance.

What worked:
- Created the dogfood goal through the dashboard form.
- Added dashboard controls for `Continue subscription handoff` and `Continue safe actions`.
- Exercised `Continue subscription handoff` through the live dashboard button, not by direct API call.
- First click prepared and started task 1, then stopped with task 1 running and no pending human input.
- Second click refreshed completed task 1, recorded passing dispatch verification, completed task 1, prepared and started task 2, then stopped with task 2 running.
- Evidence for goal `e0820db4` showed `PassedVerifications: 1`, `RunningProcesses: 1`, `PendingHumanInputCount: 0`, and task 2 running after the automatic handoff.

Direct intervention:
- Added bounded continuation endpoints:
  - `/api/goals/<goal>/advance-until-blocked`
  - `/api/goals/<goal>/advance-subscription-until-blocked`
- Added the `AdvanceLoopResultDto` response shape and reused the existing next-action automation policy/execution paths.
- Stopped the dashboard twice to release the Windows apphost lock for build/test, then restarted it on `http://localhost:5087/`.

UI/API friction:
- Demo restarts still reseed state; the first handoff dogfood goal was lost after stopping the dashboard for verification.
- The dashboard operation status did not surface the detailed `AdvanceLoopResultDto` summary in the page; evidence/next-action APIs were still needed to inspect exact stop reasons and step counts.
- Browser automation remains necessary in this headless session to prove a dashboard button was used.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed: 0 warnings, 0 errors.
- `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter FullyQualifiedName~DashboardRendererCanEmitOperatorControls` passed: 1/1.
- `dotnet test Mcg.AgentOrchestrator.sln --no-restore` passed: Core 56/56 and Infrastructure 40/40.

Next follow-up:
- Render the continuation result summary in the dashboard operation status so operators can see step count, stop reason, and blocking action without opening raw JSON.
- Consider an optional polling mode that can continue automatically after a running process exits, while still stopping at human input.

## 2026-06-04 - Full SDLC subscription validation through dashboard

Goal ID: `2fdfac29dff04a2b8ce6e47498b85bda`

Objective: Validate that the state-aware advanced process controls enhancement can pass a full SDLC lifecycle through the dashboard using subscription-backed agents.

What worked:
- Created the validation goal through the dashboard form.
- Configured Planner, Researcher, Developer, Tester, and Reviewer roles through dashboard agent/profile controls to use the `codex-cli` subscription worker.
- Ran all five SDLC tasks through dashboard task action controls.
- Answered Tester and Reviewer human-input prompts through the dashboard.
- Inspected task evidence and acceptance through the dashboard/API evidence surfaces.
- The goal reached `Completed` and `IsAccepted: true` with 5/5 passed verifications, 0 open verifications, and 0 pending human inputs.

Direct intervention:
- Stopped the running dashboard app before final verification because the Windows apphost can be locked while the demo is running.
- Ran final local verification directly: `dotnet build Mcg.AgentOrchestrator.sln --no-restore` and `dotnet test Mcg.AgentOrchestrator.sln --no-restore`.
- Restarted the demo dashboard afterward on `http://localhost:5087/`.

UI/API friction:
- Browser automation was required to reliably exercise dashboard forms and buttons in this headless session; direct API-only work is not enough for dogfooding.
- Demo restart reseeds dashboard state, so worker and agent configuration must be recreated after restarts.
- Tester and Reviewer subscription tasks requested human help for local command execution even though direct local verification was available in this session.
- After answering human input, a task with passed verification can remain `Running`, requiring manual progress completion through the dashboard.
- Retrying a task from the dashboard does not make it easy to provide the required retry message/body.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed: 0 warnings, 0 errors.
- `dotnet test Mcg.AgentOrchestrator.sln --no-restore` passed: Core 56/56 and Infrastructure 40/40.

Next follow-up:
- Improve the dashboard so subscription workflows can complete without external browser automation, make task completion after answered human input clearer, persist demo worker/profile settings across restarts, and expose retry-with-message directly in the UI.

## 2026-06-04 - State-aware advanced task controls

Task ID: `d86cf74487af4b869f717c471f82e5c2`

Objective: SDLC subscription validation: make advanced task controls state-aware so invalid start, refresh, and cancel actions are not presented as ordinary runnable controls.

Direct intervention:
- Updated the dashboard task renderer so advanced `Start prepared work`, `Refresh process`, and `Cancel process` controls are rendered as runnable buttons only when the task state supports the action.
- Invalid advanced process actions now render disabled with the readiness reason and no `data-action-button`.
- Added focused rendering coverage for prepared-dispatch, running-process, and no-process task states.

Verification:
- `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter FullyQualifiedName~DashboardRendererMakesAdvancedProcessControlsStateAware` passed: 1/1.
- `dotnet build` passed: 0 warnings, 0 errors.
- `dotnet test` passed: Core 56/56 and Infrastructure 40/40.

Blockers:
- Initial focused test attempt was blocked by a running `Mcg.AgentOrchestrator.App` process locking the apphost; stopped PID `55924` and reran successfully.

## 2026-06-04 - Goal/task loop

Goal ID: `add7e7dfbc9c48ddb2d1b477b2472d01`

Objective: Dogfood improvement: tighten the goal/task loop so operators can see and run the next task action with enough context from the dashboard.

What worked:
- The dashboard was reachable on `http://localhost:5087/`.
- `/api/goals` created an SDLC goal.
- `/api/next`, `/api/task/{task}`, and `/api/evidence` exposed enough task state to diagnose the loop.
- Running task 1 through `/api/goals/add7e7df/tasks/1/run` reproduced a prepared subscription dispatch.

What blocked progress:
- The dashboard task card treated a `Running` task with only `LastDispatch` evidence as ready for completion evidence.
- Manually calling refresh on that state failed because the task had no background process.
- Restarting the demo reseeded the isolated demo workspace, so the original dogfood goal disappeared.

Direct intervention:
- Updated the dashboard renderer so a running task with a recorded dispatch and no process shows the dispatch command, a primary `Start prepared work` button, and a brief link.
- Updated next-action control labeling from `Start worker process` to `Start prepared work`.
- Added focused rendering coverage for prepared dispatch primary actions.

UI/API friction:
- API-only creation and inspection were needed because there is no browser automation available in this session.
- If API-only work is used in future dogfood passes, the dashboard should expose equivalent discoverable controls and richer in-page confirmation so operators do not need raw JSON.
- The demo UI state reset on restart is surprising during dogfood. A visible notice or persistence toggle would make this less error-prone.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore`
- `dotnet test Mcg.AgentOrchestrator.sln --no-restore`

Follow-up:
- Make the advanced task controls state-aware too, so impossible actions like refresh without a process are hidden or disabled with the readiness reason.
- Add a persisted dogfood workspace mode separate from reseeded demo mode.

## 2026-06-04 - Post-fix live verification

Goal ID: `3e9b9b098e59414b94f5329531e091b5`

Objective: Dogfood verification: confirm prepared dispatch tasks show a usable primary next action in the dashboard goal/task loop.

What worked:
- Created the goal after restarting the dashboard.
- Ran task 1 through `/api/goals/3e9b9b09/tasks/1/run`.
- `/api/next?goal=3e9b9b09` reported `ExecuteRecordedDispatch` with control label `Start prepared work`.
- `/api/task/1?goal=3e9b9b09` showed `LastDispatch` and no `LastProcess`, matching the fixed dashboard state.
- `/api/evidence?goal=3e9b9b09` showed one dispatch-backed task and zero processes.

What blocked progress:
- A redundant rendered-page grep hit a transient sandbox spawn issue.

Direct intervention:
- None after restart; verification used the dashboard API.

UI/API friction:
- Raw JSON is still the most reliable way to capture evidence in this agent session.

Next follow-up:
- Continue the ordered dogfood list with human input context after the remaining goal/task loop follow-ups are addressed.

## 2026-06-04 - Seeded dashboard workflow check

Goal ID: `53fa03ad945c47dc8bbd1920a16decb0`

Objective: Demo: explore the agent orchestrator UI.

What worked:
- Restarted the dashboard on `http://localhost:5087/`.
- `/api/next` showed the seeded prepared dispatch with control label `Start prepared work`.
- `/api/pending-input` and `/api/human-input-worklist` showed the reviewer prompt with task context.
- Answered input `f7445c8c` through `/api/input/f7445c8c/answer`.
- `/api/pending-input` returned an empty list after the answer.
- `/api/evidence` showed pending input count `0` while preserving the prepared dispatch evidence for task 6.

What blocked progress:
- None in the dashboard/API workflow after restart.

Direct intervention:
- None.

UI/API friction:
- The advanced task controls still expose actions that may be invalid for a state; they now use consistent labels but should become state-aware.

Next follow-up:
- Continue with human input context improvements, using this seeded prompt as the baseline scenario.

## 2026-06-04 - SDLC role tuning and prototype catalog repair

Goal ID: `1de10599e734483d89c26bd65ffa89bf`

Objective: Review and tune Researcher, Tester, and Reviewer behavior; default SDLC roles to OpenAI `gpt-5.5`; set role reasoning defaults; verify through dashboard subscription-agent workflow.

What worked:
- Dashboard role configuration set Planner/Researcher/Tester/Reviewer to high reasoning and Developer to medium reasoning with OpenAI `gpt-5.5`.
- Dashboard worker-profile configuration accepted the real `codex-cli` command and reported it resolvable.
- Subscription Tester and Reviewer runs proved the model/reasoning path: logs showed `model: gpt-5.5` and `reasoning effort: high`.
- Subscription Tester Task 9 self-corrected a PowerShell filter quoting issue and produced focused verification evidence: 22/22 passing, then smaller 8/8 and 4/4 passing slices.
- Full local verification after stopping the dashboard app passed: `dotnet build Mcg.AgentOrchestrator.sln --configuration Release --no-restore`; `dotnet test Mcg.AgentOrchestrator.sln --configuration Release --no-build` with Core 57/57 and Infrastructure 49/49 passing.

What blocked progress:
- Persisted prototype `agents.json` could keep stale role definitions because startup only wrote the catalog when it was missing.
- Persisted prototype `workers.json` stayed stale until the app restarted, so dashboard-dispatched Task 8 and the first Task 9 run completed as `Write-Output {promptPath}` instead of real subscription execution.
- Several Codex subscription runs kept useful test evidence but did not exit cleanly on Windows because of repeated `windows sandbox: spawn setup refresh` errors.
- The dashboard task summary API returns HTTP 500 for out-of-range task numbers, which made task enumeration helpers noisier than necessary.

Direct intervention:
- Patched `PrototypeWorkspaceSeeder` so prototype startup repairs persisted agent catalogs on every start, preserving matching prototype-contract agents and replacing stale/mismatched roles with the OpenAI `gpt-5.5` defaults.
- Added `PrototypeWorkspaceSeeder_upgrades_stale_persisted_agent_catalog` to cover stale Anthropic/mismatched OpenAI roles and preservation of a matching custom planner.
- Corrected the live dashboard `codex-cli` profile through the dashboard after discovering the persisted profile still echoed prompt paths.
- Mistakenly used a broad `Get-Process codex | Stop-Process`, which can terminate the active Codex session. Future cleanup must use dashboard cancellation or known PIDs only.

UI/API friction:
- The dashboard can report a task as completed when the worker profile only echoed the prompt path; evidence inspection was required to catch that no implementation happened.
- The operator still needs too many helper scripts to retry, refresh, cancel, and summarize tasks through the dashboard.
- `demo-ui` is no longer the live command; `prototype-ui` is the working prototype dashboard command. The dogfood procedure and helper copy should match prototype terminology.
- Commands with `|` in verification filters need dashboard/UI quoting guidance or safer command construction.

Next follow-up:
- Add dashboard-visible warnings when a subscription worker profile is still `Write-Output {promptPath}` or otherwise looks like an echo profile.
- Make profile repair status visible after prototype startup so operators can see persisted worker/agent catalogs were upgraded.
- Improve task completion semantics so echo-only dispatches cannot masquerade as successful implementation.
- Fix out-of-range `/api/task/{number}` responses to return 404 instead of 500.
- Add a safer dashboard control for stopping/canceling spawned subscription processes without broad process-name cleanup.

## 2026-06-04 - Agent directive refresh from dogfood lessons

Goal ID: created through the dashboard form for `Dogfood process improvement: update repository agent directives from recent dogfood lessons so future agents use the dashboard/prototype workflow more safely and efficiently.`

Objective: Capture recurring dogfood lessons in repository agent directives so future agents avoid repeated inefficient or unsafe patterns.

What worked:
- Created the process-improvement goal through the hosted dashboard with auto-handoff disabled.
- Updated `AGENTS.md` with concrete directives for prototype dashboard usage, dashboard-first workflow, worker-profile inspection, evidence inspection, stuck process cleanup, Windows build locks, PowerShell filter quoting, and dogfood logging.

What blocked progress:
- The dashboard can create the goal but does not yet provide a first-class "update agent directives from recent lessons" workflow, so the directive file was patched directly.

Direct intervention:
- Edited `AGENTS.md` directly because this is repository-local agent guidance and the change depends on cross-turn conversation context not represented as an orchestrator task artifact.

UI/API friction:
- Dashboard goal creation was straightforward, but goal id extraction still requires API/script inspection rather than a prominent success summary.

Next follow-up:
- Add a visible goal-created confirmation with id/objective and direct goal link so process-only goals can be logged without helper scripts.

## 2026-06-04 - Dogfood friction burn-down implementation

Goal ID: `d82a82a24aa9463585bdc7551fff0d40`

Validation Goal ID: `a157b140bb5c4b739f9403b39f737631`

Objective: Address recurring dashboard/orchestrator dogfooding frictions around workflow visibility, worker/profile command clarity, stuck-process recovery, completion semantics, build locks, goal/task inspection APIs, PowerShell quoting, and generated artifact noise.

What worked:
- Created the friction burn-down goal through the prototype dashboard and kept it as the tracking objective.
- Added an always-visible prototype workspace panel showing execution directory, state, prompts, logs, worker profile path, and agent catalog path.
- Added a goal-level `Cancel running work` dashboard control backed by the PID-scoped `cancel-dispatches` batch operation.
- Improved dashboard operation summaries so form/button responses show goal id, action/count, or stop reason instead of a generic `Updated.`
- Changed dashboard-safe API handling so missing entities return 404 and malformed requests return 400.
- Made subscription auto-handoff stop cleanly when a real handoff cannot be prepared, instead of surfacing a 500.
- Added visible PowerShell filter quoting guidance in the operator area and verification controls.
- Updated task briefs, Researcher/Tester/Reviewer requirements, and `AGENTS.md` so source surveys exclude nested `bin`, `obj`, and `.scratch` artifacts by default.

What blocked progress:
- The live dashboard repeatedly locked app binaries during build/test until the known `Mcg.AgentOrchestrator.App` PID was stopped.
- The first browser validation proved the PowerShell hint was hidden inside collapsed task controls, so it was moved into the always-visible operator area.
- The first operation-summary implementation only handled camelCase JSON; dashboard DTOs are PascalCase, so the browser still showed `Updated.` until the summary parser handled both.
- Browser validation needed a page reload to pick up the rebuilt dashboard JavaScript.

Direct intervention:
- Patched source directly because the assigned Developer task was the implementation task and the orchestrator cannot yet patch itself without an external worker process completing the edit.
- Stopped only known dashboard app PIDs (`55664`, `58296`, `53080`) to release Windows apphost/DLL locks before build/test; no broad process-name cleanup was used.
- Added `.scratch/dashboard-validate-friction-fixes.js` to exercise the live dashboard form and visible UI affordances through the browser runner.

UI/API friction:
- Dashboard browser automation remains necessary in this headless session, but the page now exposes enough workspace, command, cancel, and operation-status context to reduce raw API helper usage.
- The app still has no first-class self-stop/restart flow for build-lock recovery; operators must stop the known dashboard PID outside the dashboard before full local verification.

Verification:
- Focused tests passed: `dotnet test --filter 'TaskBriefTests|GoalLifecycleTests|DashboardRenderingTests|DashboardHostTests'`.
- Focused dashboard tests passed after the final JS fix: `dotnet test --filter 'DashboardRenderingTests|DashboardHostTests'`.
- `dotnet build` passed with 0 warnings and 0 errors.
- `dotnet test` passed: Core 58/58 and Infrastructure 52/52.
- Restarted `prototype-ui` on `http://localhost:5087/`; health returned `200 OK`.
- Browser dashboard validation created goal `a157b140bb5c4b739f9403b39f737631` through the visible form with auto-handoff disabled and returned status `Goal a157b140 created with 1 task(s).`
- The same browser validation confirmed visible workspace context, dashboard PID/build-lock guidance, `Cancel running work`, and PowerShell quoting guidance.

Next follow-up:
- Add a dashboard-visible process-lock/build-lock notice with the exact known PID and a safe stop/restart action.
- Consider a first-class goal detail page/API response that can be opened directly from the goal-created status line.

## 2026-06-04 - Ordered dogfood friction sequence

Goal ID: `853d7a06d3d74cacb1921b87257bebb5`

Objective: Fully address the remaining repeated dogfooding frictions in sequence, starting with low-noise file surveys and reliable self-patching, then continuing through build-lock recovery, goal inspection, dashboard validation, auto-resume, task completion semantics, and retry ergonomics.

What worked:
- Created the sequence goal through the live dashboard form with auto-handoff disabled so the work stayed operator-directed.
- Added `/api/source-survey` and dashboard report links as the default low-noise repository map. The survey excludes `.scratch`, generated `bin`/`obj`, prototype state, artifacts, `.git`, and dependency caches.
- Updated task briefs to tell agents to start with the dashboard source survey before broad recursive reads.
- Validated the source survey through the dashboard browser runner; it returned 50 of 165 matched files and confirmed the exclusion guidance.
- Added patch-capability diagnostics for worker profiles and surfaced them in Setup Doctor and subscription plans.
- Developer subscription handoff now rejects Codex profiles that cannot patch the workspace because they lack `--sandbox workspace-write`, `--cd {workingDirectory}`, or prompt-content ingestion.
- Validated self-patching readiness through the dashboard browser runner; the visible subscription plan reported the Developer `codex-cli` profile as patch-capable and ready.
- Added a dashboard safe-stop endpoint and operator control that returns the exact dashboard PID and restart command, then gracefully stops the app so Windows build/test locks can be cleared without broad process cleanup.
- Added first-class `GET /api/goals/<goal-id-prefix>` goal-detail inspection and a visible `Goal JSON` link on each rendered goal card.
- Productized the dashboard browser validation harness under `scripts\Run-DashboardBrowserScript.ps1`, `scripts\Invoke-DashboardBrowser.ps1`, and `scripts\dashboard-smoke.js`; fixed the harness so JavaScript exceptions fail the command.
- Added dashboard auto-resume scheduling: after a continuation stops only because background work is still running, the page schedules another continuation post without requiring another operator click.
- Changed completion semantics so passing verification completes a running task when no task-scoped human input remains, and answering the final human-input prompt preserves completion for an already verified task.
- Replaced the bare retry button with a retry form that requires a note.

What blocked progress:
- A broad `rg` under `.scratch` still hit locked Edge profile files. The new source survey avoids this by design, but helper scripts and future agents must prefer it.
- The dashboard app still locks binaries during build/test on Windows, so the known dashboard PID had to be stopped before focused verification.
- The first checked-in browser-harness run exposed that CDP JavaScript exceptions were being serialized but not treated as command failures.
- A dashboard rendering test still expected the auto-handoff checkbox to be checked by default; the live prototype now exposes it as creation-time configuration without defaulting it on in that test fixture.

Direct intervention:
- Patched source directly after creating the dashboard goal because these changes are implementation work and the self-patching guard itself had to be improved before relying on subscription Developer edits.
- Stopped known dashboard PID `61780` before running focused tests.
- Added scratch browser validation scripts for source-survey and self-patching readiness.
- Used the new `/api/system/stop-dashboard` workflow to stop PID `57696` and PID `63664` before verification.

UI/API friction:
- Dashboard validation still requires the scratch browser runner in this headless session, but the operator UI now exposes the source survey route and patch capability directly.
- The scratch browser runner is no longer the only reusable path; checked-in scripts now provide repeatable dashboard smoke validation.
- Goal detail no longer requires stitching together monitor, transcript, and evidence endpoints; the goal card links directly to the detail JSON.
- Build-lock recovery is now first-class enough for dogfooding, though restart is still intentionally an explicit command after the process exits.

Verification:
- Focused verification passed: `dotnet test --filter 'SourceSurveyTests|DashboardRenderingTests|DashboardHostTests|TaskBriefTests|WorkerDispatchTests|HealthInspectorTests'` passed 32/32 tests.
- Dashboard source-survey validation passed through `.scratch/dashboard-validate-source-survey.js`.
- Dashboard self-patching readiness validation passed through `.scratch/dashboard-validate-self-patching.js`.
- Dashboard build-lock stop validation passed through `.scratch/dashboard-validate-build-lock-stop.js`; the app exited after returning PID and restart command.
- Dashboard goal-detail validation passed through `.scratch/dashboard-validate-goal-detail.js`.
- Checked-in dashboard smoke passed through `.\scripts\Run-DashboardBrowserScript.ps1 .\scripts\dashboard-smoke.js`.
- Final browser validation passed through `.scratch/dashboard-validate-final-frictions.js`, covering retry-message UI and auto-resume script behavior.
- Focused core verification passed: `dotnet test tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --filter 'VerificationAndInputWorklistTests'`.
- Focused infrastructure verification passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter 'DashboardRenderingTests|DashboardHostTests|DashboardValidationHarnessTests|WorkerDispatchTests|HealthInspectorTests|SourceSurveyTests'`.
- `dotnet build` passed with 0 warnings and 0 errors after stopping the dashboard.
- `dotnet test` passed.
- Restarted `prototype-ui` on `http://localhost:5087/`; `/health` returned `200 OK`.

Next follow-up:
- The ordered recurring frictions in this pass are addressed and validated. Continue dogfooding with a real subscription-backed SDLC enhancement to see whether auto-resume plus patch-capability enforcement is enough for longer-running work, and consider server-side continuation persistence if browser-driven auto-resume is not durable enough.

## 2026-06-04 - Server-owned subscription continuation

Goal ID: `a6e8f22e40154684867a48d2e5bc6eaf`

Objective: Make subscription handoff continuation server-owned instead of browser-timer-owned, so running background processes can be refreshed and handoff can resume after exit without repeated operator clicks or an open dashboard tab.

What worked:
- Created the goal through the dashboard form with auto-handoff disabled.
- Added `DashboardContinuationService`, which starts a server-side continuation watch when `advance-subscription-until-blocked` stops only because background work is still running.
- Creation-time auto-handoff now starts the same server-side watch when it reaches a running-process pause.
- Added `/api/continuations` and a dashboard `Server continuation` panel with goal link, running/stopped status, poll count, last check, and stop reason.
- Removed browser timer re-post behavior from dashboard JavaScript; the browser now reports that server continuation is watching instead of owning the retry loop.
- Added a focused test that starts a short real background process and verifies the continuation service refreshes it, records passing verification, and completes the task.

What blocked progress:
- The first dashboard goal submission happened while multiple dashboard app processes were alive, making port `5087` unstable. Known PIDs `46604`, `69344`, and `71300` had to be stopped before restarting a single dashboard.
- Initial browser validation showed that the continuation status link was hidden when no watches existed; the link is now always visible.

Direct intervention:
- Patched source directly after creating the dashboard goal because this was orchestrator implementation work.
- Stopped known duplicate dashboard PIDs after the dashboard became unreachable.
- Used the dashboard safe-stop endpoint for later verification stops.

UI/API friction:
- The dashboard now exposes watcher status directly, but there is still no persisted watcher recovery after dashboard process restart. That is a separate durability concern from removing repeated operator clicks during a live dashboard session.

Verification:
- Focused infrastructure tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter 'AdvanceLoopTests|DashboardRenderingTests|DashboardHostTests'`.
- Browser validation passed through `.scratch/dashboard-validate-server-continuation.js`, confirming the continuation panel, `/api/continuations`, server-owned status copy, and removal of browser timer re-post code.
- `dotnet build` passed with 0 warnings and 0 errors.
- `dotnet test` passed.

Next follow-up:
- Dogfood a real subscription-backed SDLC change with server continuation enabled and inspect whether watcher status plus completion semantics are enough for multi-step work.

## 2026-06-04 - Duplicate process diagnostics and skeptical manual completion

Initial Goal ID: `c0d6423cf94641fdbf3929cffd055e29`

Corrective Goal ID: `dff33c3aa5374ba19cebf0ebdb3bf784`

Manual-only warning Goal ID: `fbe8633d387a41f58a508b0d9e68fd84`

Smoke Validation Goal ID: `b4d10973d2bf484da63f6536340e3f33`

Objective: Remove recurring duplicate-dashboard-process friction and make manual-only completion visibly skeptical so no-op or evidence-only goals stop looking equivalent to implementation-backed work.

What worked:
- Created the duplicate-process goal through the dashboard, then inspected its logs/evidence after auto-handoff marked it complete.
- Created corrective goal `dff33c3a` through the dashboard after discovering the first goal completed without a source implementation.
- Added `/api/system/processes`, a dashboard process inspector, and operator-panel rendering for current PID, sibling dashboard PIDs, executable path, and safe exact-PID cleanup guidance.
- Started a second prototype dashboard on port `5098` only for validation. Browser validation confirmed the primary dashboard showed sibling PID `65696` and safe cleanup guidance.
- Added a completion-banner warning for completed goals whose only proof is manual verification and whose evidence has no execution, dispatch, or process record.
- Browser validation confirmed the manual-only warning appears near the previously completed corrective goal.
- Completed both corrective goals through dashboard task completion/verification forms.

What blocked progress:
- The initial subscription task asked for sandbox/environment help and then the goal completed with no implementation. Evidence inspection caught the no-op.
- The dashboard foreground timeout left duplicate app processes running, proving the process diagnostic was needed.
- The dashboard self-stop endpoint returned PID `70548`, but another exact app PID (`50792`) still held the app DLL and had to be stopped before verification.
- A parallel build/test attempt created transient `testhost` assembly locks; sequential build/test produced the clean signal.
- I accidentally ran one `rg` without the local `bin`/`obj`/`.scratch` exclusions and got generated-output noise again.

Direct intervention:
- Patched source directly after creating dashboard goals because these were dashboard/orchestrator implementation changes and the first subscription-backed task falsely completed.
- Stopped only exact known PIDs: stale dashboard PIDs `33964`, `63124`, validation sibling PID `65696`, stray Release PID `50108`, and lock-holding app PID `50792`.
- Used dashboard self-stop for PID `70548` before verification, then restarted `prototype-ui` on `http://localhost:5087/`.

UI/API friction:
- A completed goal with only manual verification was too easy to trust before this fix. The dashboard now warns when completion has no execution, dispatch, or process proof.
- Background dashboard startup via `Start-Process` can fail silently; foreground startup stayed alive and revealed the dashboard was healthy, but it also left duplicate processes after command timeout.
- The process diagnostic is visible and machine-readable now, but it does not identify which sibling owns which port. It uses process name and executable path, which is enough for safe exact-PID cleanup but not full port/workspace attribution.
- Sandbox spawn failures still interrupt harmless reads and process checks.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed with 0 warnings and 0 errors.
- Focused dashboard tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter 'DashboardRenderingTests|DashboardHostTests'`.
- `dotnet test Mcg.AgentOrchestrator.sln --no-build` passed.
- Browser validation passed through `.scratch/dashboard-validate-process-diagnostic.js`, proving the dashboard displayed sibling PID `65696`.
- Browser validation passed through `.scratch/dashboard-validate-manual-only-warning.js`, proving the warning copy rendered on the completed manual-only goal.
- Checked-in dashboard smoke passed through `.\scripts\Run-DashboardBrowserScript.ps1 .\scripts\dashboard-smoke.js`, creating validation goal `b4d10973d2bf484da63f6536340e3f33`.
- Final dashboard health returned `200 OK`; one dashboard app process remained, PID `55856`.

Next follow-up:
- Consider extending process diagnostics with port ownership when it can be done safely on Windows.
- Continue reducing helper-script dependence for common dashboard actions; browser automation is still necessary in this headless session.

## 2026-06-04 - Process port attribution and safer stop feedback

Goal ID: `eb2b0ab5669748618f2447477da2fd32`

Objective: Extend dashboard process diagnostics with listening port attribution and safer stop feedback so duplicate prototype app processes can be mapped to ports before exact-PID cleanup.

What worked:
- Created the goal through the live dashboard with auto-handoff disabled.
- Added best-effort listening port attribution to `/api/system/processes` by parsing `netstat -ano -p TCP`.
- Updated the operator panel to show the current dashboard PID, current listening ports, sibling PIDs, sibling listening ports, and exact `Stop-Process -Id <pid>` guidance.
- Updated `/api/system/stop-dashboard` so the stop response includes the current listening ports, sibling process list, and sibling exact stop commands.
- Live browser validation proved the dashboard mapped current PID `67496` to port `5087`, sibling PID `25384` to port `5098`, and rendered `Stop-Process -Id 25384`.
- The new stop response later revealed hidden sibling PID `59540` listening on port `5188`, which was then stopped by exact PID before final build/test.

What blocked progress:
- Background dashboard startup again left two app processes that were not serving port `5087`; exact PIDs `65404` and `68376` had to be stopped before a clean foreground restart.
- Several harmless PowerShell process/JSON reads hit `windows sandbox: spawn setup refresh`.
- A nested collection expression in a new test confused the compiler; rewriting the fake inputs as locals fixed it.

Direct intervention:
- Patched source directly after creating the dashboard goal because this was dashboard/orchestrator implementation work.
- Stopped only exact known PIDs during validation and cleanup: `25384`, `59540`, `65404`, `68376`, and stale sibling `34412`.
- Used the dashboard stop endpoint to stop current PID `67496` before final build/test.

UI/API friction:
- Process diagnostics now identify PID-to-port ownership, but workspace attribution for sibling processes is still limited to executable path unless a future Windows-safe command-line/workspace lookup is added.
- The dashboard now exposes exact stop commands, but it still does not perform a full coordinated restart that waits for stale sibling cleanup and verifies the target port is serving again.
- Browser helper scripts are still needed for reliable headless dashboard validation and task completion.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed with 0 warnings and 0 errors.
- Focused dashboard tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter 'DashboardRenderingTests|DashboardHostTests'`.
- `dotnet test Mcg.AgentOrchestrator.sln --no-build` passed.
- Browser validation passed through `.scratch/dashboard-validate-port-process-diagnostic.js`.
- Checked-in dashboard smoke passed through `.\scripts\Run-DashboardBrowserScript.ps1 .\scripts\dashboard-smoke.js`.
- Final dashboard health returned `200 OK`; one dashboard app process remained, PID `63092`.

Next follow-up:
- Add coordinated dashboard restart/cleanup workflow that uses the process diagnostic, waits for stale app PIDs to exit, and verifies the target port before reporting success.
- Continue reducing browser-helper dependence for routine goal completion and dashboard verification.

## 2026-06-04 - Build/test cleanup plan in dashboard

Goal ID: `21ffee365bc84955b8e1e58e8bde6045`

Objective: Add coordinated dashboard build-test cleanup guidance that uses process diagnostics to list current and sibling PIDs/ports, exact stop commands, restart command, and a verification checklist before running build/test.

What worked:
- Created the goal through the live dashboard with auto-handoff disabled.
- Added `/api/system/build-test-cleanup`, a read-only plan endpoint that reports current PID/ports, sibling PIDs/ports, exact stop commands, build/test commands, restart command, and an ordered checklist.
- Rendered a `Build/test cleanup plan` details panel in the operator workspace with the same ordered sequence.
- Improved dashboard operation status for stop responses so clicking stop reports PID, ports, sibling count, and restart command instead of a generic update.
- Browser validation confirmed the cleanup plan was visible and the endpoint returned PID `53308`, port `5087`, and six checklist steps.
- Completed the dashboard goal through the task verification form.

What blocked progress:
- The dashboard became unreachable before build/test while stale app processes remained. Exact stale PIDs `40264`, `67512`, and `68528` were identified; `68528` exited before stop and the remaining PIDs required exact cleanup.
- Background restart again left a transient sibling PID `70740`; the cleanup endpoint surfaced it, and it exited before manual stop.
- Browser validation initially failed because the checklist is inside a collapsed `<details>` element; opening the details element matched real operator behavior.

Direct intervention:
- Patched source directly after creating the dashboard goal because this was dashboard/orchestrator implementation work.
- Stopped only exact stale app PIDs before verification.

UI/API friction:
- The workflow is now first-class as guidance and API data, but not yet a single transactional restart operation that stops, waits, builds/tests, restarts, and confirms health.
- A collapsed details body can make automation miss visible-on-demand content unless tests explicitly open it.
- Browser helper scripts remain necessary for headless dashboard validation and task completion.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed with 0 warnings and 0 errors.
- Focused dashboard tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter 'DashboardRenderingTests|DashboardHostTests'`.
- `dotnet test Mcg.AgentOrchestrator.sln --no-build` passed.
- Browser validation passed through `.scratch/dashboard-validate-build-test-cleanup-plan.js`.
- Checked-in dashboard smoke passed through `.\scripts\Run-DashboardBrowserScript.ps1 .\scripts\dashboard-smoke.js`.
- Final dashboard health returned `200 OK`; one dashboard app process remained, PID `53308`.

Next follow-up:
- Replace guidance-only cleanup with a coordinated external helper or dashboard-driven workflow that can wait for app PIDs to exit and restart the dashboard cleanly.
- Continue reducing browser-helper dependence for routine dashboard validation.

## 2026-06-04 - Coordinated dashboard build/test cycle helper

Goal ID: `bfdfe83d0bad495fad5067a1260e74c8`

Objective: Add a coordinated build/test cycle helper that consumes the dashboard cleanup plan, stops exact dashboard PIDs, runs build/test sequentially, restarts the prototype dashboard, and verifies health.

What worked:
- Created the goal through the live dashboard with auto-handoff disabled.
- Added `scripts\Invoke-DashboardBuildTestCycle.ps1`.
- The helper consumes `/api/system/build-test-cleanup`, posts `/api/system/stop-dashboard`, waits for the current dashboard process to exit, stops only exact sibling PIDs from the cleanup plan/stop response, verifies no app process remains, runs `dotnet build` and `dotnet test` sequentially, restarts the dashboard using the dashboard-provided restart command, and waits for `/health`.
- Surfaced the helper as `BuildTestCycleCommand` in `/api/system/build-test-cleanup`.
- Rendered the helper command in the dashboard build/test cleanup plan.
- Browser validation confirmed the helper command was visible in the dashboard and present in the cleanup endpoint checklist.
- Ran the helper end to end; it returned exit code `0` after stopping the dashboard, running build/test, restarting `prototype-ui`, and verifying health.
- Completed the dashboard goal through its task verification form.

What blocked progress:
- The first completion script submitted the form but the follow-up fetch failed because the dashboard process exited; after restart, rerunning the completion script recorded the goal successfully.
- Dashboard smoke briefly failed with a source-survey fetch error while health was initially still OK; shortly after, the dashboard was unreachable and no app process was running. A foreground restart restored a stable single dashboard process, and smoke then passed.
- PowerShell sandbox spawn issues still appeared intermittently during unrelated process checks.

Direct intervention:
- Patched source and scripts directly after creating the dashboard goal because this was dashboard/tooling implementation work.
- Used the dashboard stop endpoint before compiling changed app code.
- Ran `.\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl http://localhost:5087/` as the primary verification of the new helper.

UI/API friction:
- The coordinated helper removes most of the manual stop/build/test/restart sequence, but it is still an external script launched from the shell rather than a dashboard-hosted long-running operation.
- Background dashboard startup can still be less stable than foreground startup in this sandboxed session.
- Browser helper scripts are still needed for headless form completion and UI validation.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed with 0 warnings and 0 errors.
- Focused tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter 'DashboardRenderingTests|DashboardHostTests|DashboardValidationHarnessTests'`.
- `dotnet test Mcg.AgentOrchestrator.sln --no-build` passed.
- Browser validation passed through `.scratch/dashboard-validate-build-test-cycle-helper.js`.
- Full helper validation passed through `.\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl http://localhost:5087/`.
- Checked-in dashboard smoke passed through `.\scripts\Run-DashboardBrowserScript.ps1 .\scripts\dashboard-smoke.js` after the final foreground dashboard restart.
- Final dashboard health returned `200 OK`; one dashboard app process remained, PID `13236`, listening on port `5087`.

Next follow-up:
- Reduce browser-helper dependence for routine dashboard goal completion and validation by adding first-class dashboard/API workflows for common dogfood actions.
- Consider making the coordinated build/test cycle a dashboard-owned background operation once the dashboard can survive or supervise its own restart boundary.

## 2026-06-04 - Reusable dashboard dogfood action helper

Goal ID: `f9a982d6b93545279cef65a56da69a6f`

Validation Goal ID: `f906837fcfaf498694f8c356c847c513`

Objective: Reduce bespoke scratch browser scripts by adding a checked-in reusable dashboard dogfood action helper for common create-goal, complete-task, and smoke validation actions.

What worked:
- Created the implementation goal through the live dashboard with auto-handoff disabled.
- Added `scripts\Invoke-DashboardDogfoodAction.ps1` with `create-goal`, `complete-task`, and `smoke` actions backed by the checked-in dashboard browser harness.
- Updated `AGENTS.md` and `README.md` so routine dogfood actions prefer the reusable helper before one-off scratch scripts.
- Used the helper itself to create validation goal `f906837fcfaf498694f8c356c847c513`, complete task 1 through the dashboard completion form, and run checked-in dashboard smoke.
- Completed this dogfood goal through the new helper, proving the helper can close the loop it was created to support.

What blocked progress:
- The live helper exposed strict-mode bugs in `Invoke-DashboardBrowser.ps1`: DevTools event frames do not include `id`, normal evaluation responses may omit `exceptionDetails`, and async WebSocket calls leaked `VoidTaskResult` output.
- A stale intermediate endpoint edit left `DashboardEndpoints.Goals.cs` referencing a nonexistent `ReportGoalProgress` method during validation; replacing it with a non-fatal operator log restored compilation.
- The first hidden dashboard starts and several harmless checks hit intermittent `windows sandbox: spawn setup refresh`.
- One create-goal attempt caused the dashboard to become unreachable before redirected logging was enabled; a clean rebuild and redirected restart stabilized the next validation.

Direct intervention:
- Patched `Invoke-DashboardBrowser.ps1` after the dashboard-goal was created so it ignores CDP event frames, guards optional exception properties, suppresses async task output, and uses `CloseOutputAsync`/`Abort` during cleanup.
- Patched `DashboardValidationHarnessTests` to keep the browser-wrapper and reusable-helper safeguards covered.
- Patched the dashboard auto-handoff catch path to log startup failure instead of calling an unavailable helper or marking a task failed.

UI/API friction:
- The reusable helper reduces custom `.scratch` browser scripts, but it is still external browser automation rather than an in-dashboard first-class operation runner.
- Dashboard startup can still be fragile in this sandbox when launched hidden without redirected logs.
- Create/complete helper runs are slow because each browser action waits for page-side form handling and evidence fetches.

Verification:
- Focused harness test passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter DashboardValidationHarnessTests`.
- Full solution validation passed: `dotnet build Mcg.AgentOrchestrator.sln --no-restore` and `dotnet test Mcg.AgentOrchestrator.sln --no-build` passed.
- Coordinated dashboard build/test cycle passed through `.\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl http://localhost:5087/`, including dashboard stop, build, test, restart, and health check.
- Live helper validation passed for `create-goal`, `complete-task`, and `smoke`; validation goal `f906837fcfaf498694f8c356c847c513` reached `Completed` with `VerificationSatisfied: true`.
- Dogfood goal `f9a982d6b93545279cef65a56da69a6f` reached `Completed` with passed verification through the helper.

Next follow-up:
- Move the next recurring friction from external helper scripts toward dashboard-owned long-running operations, starting with the build/test cycle.
- Continue the required subscription-backed SDLC validation once dashboard-owned operation supervision is less brittle.

## 2026-06-04 - Dashboard-owned build/test cycle launcher

Goal ID: `dd9625e5349f43e2b60a3ec892724735`

Objective: Make the coordinated build-test cycle a dashboard-owned operation instead of a shell-only helper, with operator-visible status and exact PID safety.

What worked:
- Created the goal through the dashboard using `Invoke-DashboardDogfoodAction.ps1`.
- Added `POST /api/system/run-build-test-cycle`, which writes a runner script under the orchestrator log directory and starts `scripts\Invoke-DashboardBuildTestCycle.ps1` as an independent background PowerShell process.
- Extended `/api/system/build-test-cleanup` with `RunBuildTestCycleUrl`.
- Added the dashboard button `Run dashboard build/test cycle` and rendered the run endpoint in the build/test cleanup plan.
- Updated dashboard operation status summarization so a successful launch reports PID, command, and output/error log paths.
- Exercised the new dashboard button path through the loaded dashboard page. It created `src\Mcg.AgentOrchestrator.App\.orchestrator-prototype\workspace\.orchestrator\logs\dashboard-build-test-cycle-20260604-161905.out.log` and restarted the dashboard.

What blocked progress:
- A focused test run hit the known Windows apphost lock from live dashboard PID `71224`; the existing coordinated helper handled the stop/build/test/restart sequence.
- The first browser click returned an empty status, but the later dashboard `post(button.dataset.actionButton)` path started the run; the WebSocket command timed out because the dashboard intentionally stopped during the cycle.
- Completing the tracking goal through the browser helper hung twice, so the final bookkeeping completion used the task API directly. That means the helper still needs hardening for long-lived/stale browser sessions after dashboard restarts.
- Broad hidden file discovery produced very noisy output from old prototype logs; future searches should use narrower paths and avoid browser profile directories.

Direct intervention:
- Patched system endpoints, DTOs, renderer, dashboard JS, and focused tests directly after creating the dashboard goal because this was dashboard/orchestrator implementation work.
- Used the existing external build/test cycle helper once to clear the live apphost lock and validate the implementation.
- Used direct API completion for goal `dd9625e5349f43e2b60a3ec892724735` after the dashboard helper repeatedly hung on the completion form.

UI/API friction:
- The dashboard can now launch the build/test cycle, but progress after the dashboard stops is visible only by reading log files after restart.
- The dashboard does not yet render recent dashboard-owned build/test run history or tail the output/error logs.
- The browser helper needs stale-tab/session recovery after dashboard restart so routine completion does not require API fallback.

Verification:
- Focused tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter "DashboardRenderingTests|DashboardHostTests"` passed 12/12.
- Full coordinated validation passed through `.\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl http://localhost:5087/`.
- Dashboard-owned run log `dashboard-build-test-cycle-20260604-161905.out.log` shows `Build succeeded.`, Core tests `60/60`, Infrastructure tests `61/61`, `BuildSucceeded: True`, and `TestSucceeded: True`.
- Dashboard health returned `200 OK` after the dashboard-owned cycle restarted the prototype UI.

Next follow-up:
- Add dashboard-visible build/test run history and log preview/tail after restart.
- Harden `Invoke-DashboardDogfoodAction.ps1`/`Invoke-DashboardBrowser.ps1` so post-restart completion actions recover stale browser sessions instead of hanging.

## 2026-06-04 - Dashboard build/test run history and previews

Goal ID: `ff10f8c26af3445ebea24bf4eda7b737`

Objective: Add dashboard-visible build/test run history and log previews for dashboard-owned build/test cycles after restart.

What worked:
- Created the goal after the live dashboard helper hung; direct API creation was used as fallback and recorded as friction.
- Added `/api/system/build-test-runs`, which groups `dashboard-build-test-cycle-*` runner/output/error files and reports status, pass/fail flags, paths, and output/error previews.
- Added `/api/system/build-test-runs/log`, constrained to dashboard build/test run files under the orchestrator log directory.
- Rendered a `Build/test run history` panel in the operator dashboard with recent run status, output/error links, and preview snippets.
- Bounded live operator dashboard rendering to the eight most recent goals while preserving static export behavior, because the persisted prototype state had grown enough that the full live page timed out.

What blocked progress:
- `Invoke-DashboardDogfoodAction.ps1` timed out while creating the tracking goal; the goal was not created, so direct API fallback was required.
- Browser harness inspection of the rendered dashboard still hung after dashboard restarts, confirming stale browser/session recovery remains unresolved.
- The first full page fetch timed out at 60 seconds before bounded live rendering was added.
- A full coordinated build/test run initially failed with a transient hosted-dashboard 404 in the full suite; the host test passed in isolation and the subsequent full suite passed.

Direct intervention:
- Patched dashboard API, DTOs, renderer, and tests directly after creating the tracking goal because this was dashboard/orchestrator implementation and an operator-page performance blocker.
- Used direct API completion for goal `ff10f8c26af3445ebea24bf4eda7b737` because dashboard/browser-helper completion remains unreliable after restarts.

UI/API friction:
- Dashboard now shows run history and log previews, but it does not yet stream live progress while the dashboard is stopped during a cycle.
- The browser helper still needs stale-tab/session recovery and timeout handling before it can be trusted for routine post-restart create/complete actions.
- The operator page is now bounded, but older goal inspection still depends on goal JSON/report endpoints rather than an in-page archive browser.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed with 0 warnings and 0 errors.
- Focused dashboard tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter "DashboardRenderingTests|DashboardHostTests"`.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build`.
- `/api/system/build-test-runs` returned prior run `20260604-161905` with `BuildSucceeded: true` and `TestSucceeded: true`.
- `/api/system/build-test-runs/log` returned the output log for `dashboard-build-test-cycle-20260604-161905.out.log`.
- Full dashboard page returned `200 OK` within normal timeout after bounded live rendering; response links included `Open build/test run JSON` and output/error log links.

Next follow-up:
- Fix stale browser/helper recovery so dashboard dogfood create/complete actions work reliably after dashboard restarts.
- Continue toward another subscription-backed SDLC validation once routine dashboard automation is stable again.

## 2026-06-04 - Stale browser helper recovery

Goal ID: `13457c24f8644e2fa00db2e4710f451c`

Objective: Harden the checked-in dashboard browser and dogfood action helpers so create, complete, and smoke recover after dashboard restarts instead of hanging on stale browser sessions.

What worked:
- Created the dogfood goal through the dashboard fallback path, then validated the repaired helper against the live dashboard.
- Updated `Invoke-DashboardBrowser.ps1` to open a fresh DevTools target per action, wait for dashboard readiness, time out CDP receives, fail on DevTools protocol errors, retry transient navigation context loss, and close the created target.
- Added a dashboard JavaScript readiness marker and `window.__dashboardSubmitForm` so browser automation can submit the visible dashboard form without native page navigation.
- Added dashboard `?goal=<prefix>` focus rendering so older active goals remain operable after the live page is bounded to recent goals.
- Updated `Invoke-DashboardDogfoodAction.ps1` so completion opens the focused goal URL and submits the rendered completion form through the dashboard hook.
- Live validation completed goal `13457c24f8644e2fa00db2e4710f451c` through the dashboard helper and smoke passed afterward.

What blocked progress:
- The first completion attempt failed because bounded rendering hid older goal forms.
- The next attempts exposed a DevTools `Execution context was destroyed` navigation race and showed that protocol `error` frames were previously printed as success.
- Native `form.requestSubmit()` could still reset the execution context; the helper needed an explicit dashboard submit hook.
- Generated `artifacts` and `.scratch` trees were being included by default project item globs; `Directory.Build.props` now excludes them to avoid duplicate generated assembly attributes.

Direct intervention:
- Patched dashboard renderer, page endpoint, dashboard JS assets, browser helper, dogfood action helper, `Directory.Build.props`, and focused tests after the dogfood goal was created.
- Stopped the dashboard through `/api/system/stop-dashboard` before build/test cycles to avoid Windows apphost locks.

UI/API friction:
- The dashboard can now operate on older goals via `?goal=<prefix>`, but the in-page archive is still just a focus query rather than a first-class goal picker.
- Browser automation is much more reliable, but it still depends on an external headless Edge/CDP bridge instead of a native dashboard test harness.
- Build/test cycle orchestration still stops the dashboard, so live progress is visible after restart via run history rather than streamed during execution.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed.
- Focused tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter "DashboardRenderingTests|DashboardValidationHarnessTests"`.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build`.
- Live dashboard completion helper completed and verified goal `13457c24f8644e2fa00db2e4710f451c`.
- Live dashboard smoke passed with `sourceSurveyReturnedFiles: 25` and `sourceSurveyTotalMatchedFiles: 258`.

Next follow-up:
- Continue dogfood friction removal with another subscription-backed SDLC validation now that create/complete/smoke automation is reliable again.
- Consider replacing the CDP helper with an in-repo browser test dependency if further dashboard interaction races recur.

## 2026-06-04 - Dashboard goal archive and focus control

Goal ID: `ae310f6c39954a69b3511b94bc2df852`

Objective: Add a dashboard goal archive and focus control so older goals can be found and operated without manually editing the URL.

What worked:
- Created the goal through the dashboard helper as a full SDLC workflow with automatic subscription handoff enabled.
- Auto handoff did launch `codex-cli` for the Planner task with `gpt-5.5` and high reasoning, proving the dashboard creation path can start subscription dispatch.
- Added a lightweight `Goal Archive` section to the operator dashboard with a GET form, `datalist` of goal prefixes/objectives, and quick `?goal=<prefix>` links.
- Kept full goal-card rendering bounded while allowing focused older goals to render their task controls.
- Completed Planner, Researcher, Developer, Tester, and Reviewer tasks through the dashboard helper with concrete evidence.

What blocked progress:
- The subscription Planner dispatch failed because Codex reported an external usage limit: `You've hit your usage limit... try again at 4:58 PM.`
- Continuation stopped with `Failed task inspection is required before automated advance`; no automatic recovery classified the usage-limit failure as retry-later.
- Live HTML inspection through PowerShell still hit intermittent sandbox spawn failures, requiring a retry/escalation for a read-only check.

Direct intervention:
- Implemented `RenderGoalArchiveControl` directly after the dashboard goal existed because the subscription agent was externally blocked.
- Patched `DashboardRenderingTests` directly to assert the archive control, datalist option, quick focus link, and focused completion-control behavior.
- Stopped/restarted the dashboard around build/test to avoid Windows apphost locks.

UI/API friction:
- The archive is functional but basic; it is a datalist plus quick links rather than a richer searchable/filterable goal browser.
- Subscription usage-limit failures are still treated as ordinary task failures requiring human inspection; this should become a retry-later/recoverable dispatch state.
- The dashboard can start subscription SDLC handoff, but a full end-to-end subscription-authored implementation is still blocked until Codex usage is available.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed with 0 errors.
- Focused rendering tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter DashboardRenderingTests` passed 12/12.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build` passed Core 60/60 and Infrastructure 63/63.
- Live dashboard page `http://localhost:5087/?goal=ae310f6c` rendered `Goal Archive`.
- Dashboard smoke passed after goal completion with `sourceSurveyReturnedFiles: 25` and `sourceSurveyTotalMatchedFiles: 258`.

Next follow-up:
- Add recoverable failed-task handling for subscription usage limits and other retry-later errors.
- Re-run a full subscription-authored SDLC enhancement after the subscription usage limit clears.

## 2026-06-04 - Recoverable subscription usage-limit failures

Goal ID: `5c106d761eba4de0b3a66d206c1dbf81`

Objective: Classify subscription usage-limit dispatch failures as recoverable retry-later states instead of hard task failures.

What worked:
- Created the goal through the dashboard helper.
- Reproduced the concrete blocker from goal `ae310f6c39954a69b3511b94bc2df852`: Codex subscription dispatch exited 1 with `You've hit your usage limit... try again`.
- Updated `RecordDispatchExecutionResult` so failed dispatch output containing `usage limit` plus `try again` or `purchase more credits` records the failed verification in history, clears latest verification, reopens the task to Assigned/Pending, appends `TaskRetried`, and does not emit `TaskFailed`.
- Completed the dogfood goal through the dashboard helper after validation.

What blocked progress:
- The live subscription limit still prevents a full subscription-authored implementation run right now.
- The retry-later state is represented with existing `Assigned` plus `TaskRetried` semantics rather than a dedicated `RetryLater` task status.

Direct intervention:
- Patched core kernel behavior and core tests directly after creating the dashboard goal because this fixes the orchestrator’s own failed-task recovery path.

UI/API friction:
- Dashboard evidence will now show retry history, but there is not yet a dedicated visual badge for “retry later due to provider/subscription limit.”
- Future recovery could parse a suggested retry timestamp from provider output and avoid immediate auto-handoff loops until that time.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed.
- Focused core dispatch tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --filter DispatchExecutionTests` passed 8/8.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build` passed Core 61/61 and Infrastructure 63/63.
- Dashboard helper completed and verified goal `5c106d761eba4de0b3a66d206c1dbf81`.

Next follow-up:
- Add dashboard visibility for recoverable retry-later provider failures.
- Re-run subscription SDLC once the Codex usage limit clears to verify the new retry behavior in a live handoff loop.

## 2026-06-04 - Retry-later visibility in dashboard evidence

Goal ID: `f4853c89a09b415fa083703eb3db1fa6`

Objective: Surface recoverable subscription retry-later failures clearly in dashboard task evidence and attention views.

What worked:
- Created the goal through the dashboard helper and completed it through the dashboard helper after validation.
- Added `DispatchFailureClassifier` so kernel recovery and dashboard evidence share the same subscription usage-limit detection.
- Updated goal evidence summaries to show `Recoverable subscription usage limit; task is ready to retry later.`
- Updated task evidence rendering to show a visible `Retry later` block for Assigned/Pending tasks whose latest verification history is a recoverable subscription limit.

What blocked progress:
- A first focused infrastructure test command used a filter that did not match the new test display/method name; reran the full `DashboardRenderingTests` class instead.
- Live subscription verification is still blocked by the current Codex usage limit.

Direct intervention:
- Patched core classifier/evidence logic, dashboard rendering, and tests directly after creating the dashboard goal.

UI/API friction:
- The dashboard now shows retry-later evidence, but it still does not parse or display the provider’s suggested retry timestamp as a scheduler hint.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed.
- `dotnet test tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --filter DispatchExecutionTests` passed 8/8.
- `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter DashboardRenderingTests` passed 13/13.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build` passed Core 61/61 and Infrastructure 64/64.

Next follow-up:
- Add retry timestamp parsing/scheduling for provider-limit failures if auto-handoff loops retry too aggressively.
- Re-run a subscription-authored SDLC goal after the Codex usage limit clears.

## 2026-06-04 - Retry-window scheduling for subscription usage limits

Goal ID: `da3b786ef34e42709e1791fd6050e3f2`

Objective: Parse provider retry timing from subscription usage-limit failures and prevent auto-handoff from retrying before the suggested time.

What worked:
- Created and completed the goal through the dashboard helper.
- Extended `DispatchFailureClassifier` to parse provider text such as `try again at 4:58 PM` into a retry-after timestamp based on the failed verification time.
- Updated `WorkerProfileDispatcher` so batch subscription preparation skips usage-limited assigned tasks before the retry-after time, and single-task subscription preparation rejects early retries with a clear `retry after` message.
- Updated subscription plan detail to report `Recoverable subscription usage limit; retry after ...`.
- Updated dashboard task evidence to show `Retry later` plus the parsed retry-after timestamp when available.

What blocked progress:
- A first parallel validation run locked `src\Mcg.AgentOrchestrator.Core\obj\Debug\net10.0\Mcg.AgentOrchestrator.Core.dll` through `VBCSCompiler` PID `84232`; stopped that exact compiler server process and reran validation sequentially.
- Live subscription execution remains blocked by the current Codex usage limit, so this pass used deterministic core/infrastructure tests for retry-window behavior rather than a real provider retry.

Direct intervention:
- Patched core classifier logic, infrastructure dispatcher gating, dashboard subscription-plan detail, dashboard evidence rendering, and focused tests directly after creating the dashboard goal.

UI/API friction:
- Retry-after parsing currently handles common `try again at <time>` text, but does not yet parse every possible provider format or preserve the retry-after as structured persisted state.
- The dashboard shows the retry-after but does not yet have a scheduler queue or countdown.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed after rerunning sequentially.
- `dotnet test tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --no-build --filter DispatchExecutionTests` passed 8/8.
- `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter "WorkerDispatchTests|DashboardRenderingTests"` passed 25/25.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build` exited 0.

Next follow-up:
- Consider persisting provider retry-after as structured task metadata if more provider formats appear.
- Re-run a subscription-authored SDLC goal after the Codex usage limit clears.

## 2026-06-04 - Structured subscription retry-after metadata

Goal ID: `c8e13a1ddf7e4ddeb32a5c8930916c94`

Objective: Persist recoverable subscription retry-after as structured task metadata so dashboard and auto-handoff do not depend on reparsing provider stderr.

What worked:
- Created and completed the goal through the dashboard helper.
- Added `TaskSpec.SubscriptionRetryAfter` and persisted it through `TaskSnapshot`.
- Set the timestamp when recoverable subscription usage-limit dispatch evidence includes a provider retry time.
- Updated `DispatchFailureClassifier` so dispatcher, subscription plan, dashboard evidence, and API summaries use structured metadata first, with provider text parsing retained as fallback for older state.
- Exposed `SubscriptionRetryAfter` on task summary API DTOs; the live dashboard completion response included the new field.

What blocked progress:
- Live subscription execution is still blocked by the external Codex usage limit, so validation used deterministic dispatch evidence instead of a real provider retry.

Direct intervention:
- Patched core domain/snapshot/classifier/recording behavior, dashboard task DTO mapping, and focused tests directly after creating the dashboard goal because this is orchestrator self-recovery behavior.

UI/API friction:
- The dashboard now exposes retry timing as structured task/API data, but it still does not show a scheduler queue/countdown or persisted background retry plan.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed.
- `dotnet test tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --no-build --filter DispatchExecutionTests` passed 9/9.
- `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter "WorkerDispatchTests|DashboardRenderingTests"` passed 25/25.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build` exited 0.
- Restarted `prototype-ui` on `http://localhost:5087/`; `/health` returned `200 OK`.
- Dashboard helper completed and verified goal `c8e13a1ddf7e4ddeb32a5c8930916c94`.

Next follow-up:
- Add a dashboard-visible scheduler/countdown for retry-deferred subscription tasks.
- Re-run a subscription-authored SDLC goal after the Codex usage limit clears.

## 2026-06-04 - Subscription retry queue visibility

Goal ID: `8c33c002a5da4bd88d7df5be7e0c4986`

Objective: Show retry-deferred subscription tasks as a dashboard-visible scheduler queue with retry-after countdown/status so operators can see why auto-handoff is waiting.

What worked:
- Created and completed the goal through the dashboard helper.
- Added subscription-plan API fields for retry-deferred count, next retry-after timestamp, per-task retry-after, and per-task delay seconds.
- Added a dashboard `Subscription retry queue` panel for goals with deferred subscription tasks, showing task number, role, retry-after time, wait duration, and description.
- Kept the queue driven by `DispatchFailureClassifier.IsSubscriptionRetryDeferred`, so UI, API, and dispatcher use the same structured-first retry logic.

What blocked progress:
- The live Codex subscription limit still prevents a real provider retry loop, so the queue was validated with deterministic recoverable usage-limit evidence.
- A stale sibling app process on port `5099` remained after stopping the 5087 dashboard; stopped exact PID `73256` before build/test to avoid output locks.

Direct intervention:
- Patched dashboard API DTO mapping, dashboard HTML rendering, and focused rendering/dispatcher tests directly after creating the dashboard goal.

UI/API friction:
- The queue is visible and countdown-like, but it is not yet a server-owned wake-up scheduler that automatically resumes a goal exactly when the retry window opens.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed.
- `dotnet test tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --no-build --filter DispatchExecutionTests` passed 9/9.
- `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter "WorkerDispatchTests|DashboardRenderingTests"` passed 25/25.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build` exited 0.
- Restarted `prototype-ui` on `http://localhost:5087/`; `/health` returned `200 OK`.
- Dashboard helper completed and verified goal `8c33c002a5da4bd88d7df5be7e0c4986`.

Next follow-up:
- Add server-owned retry wake-up/resume for deferred subscription tasks if automatic handoff still requires operator polling after the provider window opens.
- Re-run a subscription-authored SDLC goal after the Codex usage limit clears.

## 2026-06-04 - Server-owned subscription retry wake-up

Goal ID: `e3f42f4ca2c24d589ca9e3966aebcac1`

Objective: Automatically resume subscription handoff after a deferred provider retry window opens, using server-owned retry wake-up instead of operator polling.

What worked:
- Created and completed the goal through the dashboard helper.
- Added `ContinueAfter` to advance-loop results so subscription retry-window blocks carry a structured wake-up timestamp.
- Updated subscription advancement to classify deferred retry windows as continuation-worthy instead of generic `InvalidOperationException` stops.
- Updated `DashboardContinuationService` so server watches continue for retry windows, sleep until the next retry timestamp when known, and expose `NextCheckAt`.
- Updated the operator continuation table to show `Next check` for running server continuation watches.

What blocked progress:
- Live provider retry could not be exercised because the external Codex subscription limit is still active; tests use deterministic recoverable usage-limit state.
- The first dashboard helper create-goal attempt hit the recurring Windows sandbox spawn failure and required rerun with escalation.

Direct intervention:
- Patched dashboard advance-loop DTOs, continuation service behavior, continuation rendering, and focused advance-loop/rendering tests directly after creating the dashboard goal.

UI/API friction:
- Server wake-up is process-local to the running dashboard session. A dashboard restart still loses in-memory continuation watches; durable continuation scheduling remains a future hardening item.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed.
- `dotnet test tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj --no-build --filter DispatchExecutionTests` passed 9/9.
- `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter "AdvanceLoopTests|WorkerDispatchTests|DashboardRenderingTests"` passed 30/30.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build` exited 0.
- Restarted `prototype-ui` on `http://localhost:5087/`; `/health` returned `200 OK`.
- Dashboard helper completed and verified goal `e3f42f4ca2c24d589ca9e3966aebcac1`.

Next follow-up:
- Persist continuation watches across dashboard restarts if retry windows or long-running subscription jobs need durability beyond a single process lifetime.
- Re-run a subscription-authored SDLC goal after the Codex usage limit clears.

## 2026-06-04 - Persisted server continuation watches

Goal ID: `29a2bab78063478990012cd6facf66ff`

Objective: Persist server continuation watches across dashboard restarts so retry-window wake-ups and long-running subscription watches survive the prototype process lifetime.

What worked:
- Created and completed the goal through the dashboard workflow with helper-backed dashboard actions.
- Added durable active-watch storage in `continuation-watches.json` under the orchestrator workspace.
- Restored persisted watches during dashboard endpoint startup.
- Kept terminal completed/failed watches out of durable state while preserving in-memory status for the current dashboard session.
- Treated process shutdown cancellation as resumable rather than terminal.

What blocked progress:
- One source read and one process stop hit the recurring Windows sandbox `spawn setup refresh` failure and needed escalation.
- The dashboard still cannot run its own build/test cycle without stopping and restarting across the process boundary.

Direct intervention:
- Edited the continuation service, dashboard endpoint bootstrap, and focused infrastructure test directly after creating the dashboard goal because this was an orchestrator self-fix.
- Fixed test assertion overloads after the first build exposed project-specific xUnit resolution behavior.

UI/API friction:
- Live validation still used API/helper calls for completion and endpoint checks, though the dashboard itself restored `/api/continuations` state after restart.
- Existing persisted continuation state caused `/api/continuations` to show a restored running watch immediately after restart, which is good recovery behavior but suggests the dashboard should make persisted-vs-new watches visually explicit.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed with 0 warnings and 0 errors.
- Focused infrastructure tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter "AdvanceLoopTests|DashboardHostTests|DashboardRenderingTests"`.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build` exited 0.
- Restarted `prototype-ui` on `http://localhost:5087/`; `/health` returned `200 OK`.
- `/api/continuations` returned a running restored continuation watch after restart, validating startup restoration in the live dashboard process.
- Dashboard helper completed and verified goal `29a2bab78063478990012cd6facf66ff`.

Next follow-up:
- Make restored continuation watches visually distinct in the dashboard, including whether they came from durable store and what retry/wait condition is driving them.
- Re-run a subscription-authored SDLC goal after the Codex usage limit clears.

## 2026-06-04 - Restored continuation watch source visibility

Goal ID: `c85a8bc4dcec4ac1965bf4415a99c59e`

Objective: Make restored server continuation watches visually distinct in the dashboard and API so operators can see when subscription handoff resumed from durable state after a prototype restart.

What worked:
- Created and completed the goal through the dashboard workflow with helper-backed dashboard actions.
- Added `RestoredFromStore` to `/api/continuations`.
- Marked watches restored from durable state and left newly started watches marked as current-process watches.
- Added a dashboard Source column that renders restored watches as `Restored from durable store`.

What blocked progress:
- The hidden dashboard restart briefly left no responding server, while a later foreground start showed the app could stay alive. A longer readiness check recovered the live process.
- Targeted live content checks hit the Windows sandbox `spawn setup refresh` failure when run in parallel and needed escalation.

Direct intervention:
- Edited the continuation DTO, continuation service, operator renderer, and focused tests directly after creating the dashboard goal.

UI/API friction:
- `/api/continuations` now exposed many restored-but-terminal stale watches after restart. The new Source column made this visible, but the durable store should prune terminal or no-longer-watchable entries before they clutter the dashboard and consume startup work.
- The dashboard page is now very large because recent completed goals, build/test logs, and continuation rows all render together; targeted validation is safer through focused endpoints.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed with 0 warnings and 0 errors.
- Focused infrastructure tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter "AdvanceLoopTests|DashboardRenderingTests"` passed 19/19.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build` exited 0.
- Restarted `prototype-ui` on `http://localhost:5087/`; `/health` returned `200 OK`.
- `/api/continuations` returned `RestoredFromStore: true` for restored watches.
- The live dashboard rendered `<th>Source</th>` and `Restored from durable store`.
- Dashboard helper completed and verified goal `c85a8bc4dcec4ac1965bf4415a99c59e`.

Next follow-up:
- Prune stale persisted continuation watches: only keep watches that still have a future retry window or running background process, and remove entries once restored evaluation reaches human-input, monitoring-only, verification, acceptance, completed, failed, or other terminal/non-watchable states.
- Reduce dashboard page weight for routine validation, likely by limiting rendered completed-goal detail outside the focused goal.

## 2026-06-04 - Restored continuation stale-row pruning

Goal ID: `60f7926ed9f1456eb2eb2a5754ab8b3a`

Objective: Prune stale persisted continuation watches so restart recovery only keeps genuinely watchable subscription handoff states and removes entries that resolve to human input, monitoring-only, verification, acceptance, completed, failed, or other terminal states.

What worked:
- Created and completed the goal through the dashboard workflow with helper-backed dashboard actions.
- Confirmed the durable `continuation-watches.json` file already contained only still-running watches.
- Fixed the remaining clutter by removing restored watches from live status after their first restored evaluation reaches a non-watchable terminal state.
- Preserved current-session terminal watch visibility, so a watch started in the current process still leaves immediate operator feedback.

What blocked progress:
- Initial test fixture used non-existent kernel methods and xUnit assertion helpers not available in the infrastructure test project; build caught this before runtime.
- One continuation API validation check hit the Windows sandbox `spawn setup refresh` failure and needed escalation.

Direct intervention:
- Edited `DashboardContinuationService` and `AdvanceLoopTests` directly after creating the dashboard goal.

UI/API friction:
- Four restored watches remain genuinely running and polling because their background task records still report running. That may be correct, but it should be audited next: if the underlying processes are gone or impossible to refresh, continuation should not poll forever.
- The main dashboard remains heavy for validation because it renders many completed goal details; focused endpoints are much cheaper.

Verification:
- `dotnet build Mcg.AgentOrchestrator.sln --no-restore` passed with 0 warnings and 0 errors.
- Focused continuation tests passed: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --filter AdvanceLoopTests` passed 7/7.
- Full suite passed: `dotnet test Mcg.AgentOrchestrator.sln --no-build` exited 0.
- Restarted `prototype-ui` on `http://localhost:5087/`; `/health` returned `200 OK`.
- `/api/continuations` returned only four running restored watches and no `Monitoring is read-only` or `Human input is required` terminal clutter.
- Dashboard helper completed and verified goal `60f7926ed9f1456eb2eb2a5754ab8b3a`.

Next follow-up:
- Audit the four still-running restored continuation watches and add stale-running-process recovery if their task processes are no longer alive or cannot make progress.
- Reduce dashboard page weight for routine validation, likely by limiting rendered completed-goal detail outside the focused goal.
## 2026-06-04 - Simple hosted dashboard goal 56e36944

- Goal/task: prototype goal `56e369447934451f90c8da392b2f5863`, Developer task `4b1fbe4bbef64674a79faf76960bc979` (`Simple hosted dashboard goal`).
- What worked: source survey narrowed the implementation surface to the hosted dashboard path. `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DashboardHostTests.cs` already covers the requested simple hosted dashboard flow by starting the prototype dashboard over Kestrel, creating `Simple hosted dashboard goal` through `/api/goals`, confirming one Developer task, reading `/api/goals/{id}`, `/api/source-survey`, dashboard assets, continuation/process/build-test endpoints, and stopping the dashboard through `/api/system/stop-dashboard`.
- Direct changes: no product source changes were needed for this task; only this dogfood entry was added.
- Blockers/friction: prior dashboard dispatch attempts for this task failed because `codex-cli` hit a recoverable subscription usage limit. The current shell also intermittently failed fresh PowerShell launches with `windows sandbox: spawn setup refresh`, so verification used the command result already emitted by `dotnet test`.
- Verification: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~DashboardHostTests.PrototypeDashboardServesHealthAndGoalJsonOverKestrel' --logger "trx;LogFileName=simple-hosted-dashboard-goal-56e36944-rerun.trx"` completed with exit code `0`.
- Next product follow-up: make the subscription retry loop surface repeated usage-limit failures more prominently before re-dispatching the same task many times.

## 2026-06-04 - Simple hosted dashboard goal e79fd113

- Goal/task: active hosted dashboard goal `Simple hosted dashboard goal`, Developer task `e79fd1136b1f46b2a12d60ee643203ac`.
- What worked: dashboard smoke validation returned a source survey summary with `sourceSurveyReturnedFiles: 25` and `sourceSurveyTotalMatchedFiles: 297`, confirming the hosted dashboard and `/api/source-survey` path were reachable before broader inspection.
- Direct changes: no product source changes were needed. The existing hosted dashboard coverage in `tests/Mcg.AgentOrchestrator.Infrastructure.Tests\DashboardHostTests.cs` already verifies creating `Simple hosted dashboard goal` through `/api/goals`, receiving one Developer task, loading `/api/goals/{id}`, `/api/source-survey`, dashboard assets, continuation/process/build-test endpoints, and stopping the hosted dashboard cleanly.
- Blockers/friction: one parallel shell launch hit the known Windows sandbox `spawn setup refresh` issue; the focused verification command succeeded when rerun as a single command. `git status --short` could not run because this workspace does not expose a `.git` directory.
- Verification: `.\scripts\Invoke-DashboardDogfoodAction.ps1 smoke` exited `0` and returned dashboard/source-survey evidence. `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~DashboardHostTests.PrototypeDashboardServesHealthAndGoalJsonOverKestrel' --logger "trx;LogFileName=simple-hosted-dashboard-goal-e79fd113-rerun.trx"` exited `0`.
- Next product follow-up: keep using the dashboard helper for routine smoke and completion actions; no new product follow-up was found for this simple hosted-dashboard path.

## 2026-06-04 - Simple hosted dashboard goal task 74896bbf

- Goal/task: active dashboard goal `Simple hosted dashboard goal`, Developer task `74896bbf994e4ba5a8c712726f7b1c05`.
- What worked: `tests\Mcg.AgentOrchestrator.Infrastructure.Tests\DashboardHostTests.cs` already covers the requested simple hosted dashboard behavior by starting `prototype-ui` over Kestrel, creating `Simple hosted dashboard goal` through `/api/goals`, confirming one Developer task, reading `/api/goals/{id}`, `/api/source-survey`, dashboard CSS/JS assets, continuation/process/build-test endpoints, and stopping the hosted dashboard through `/api/system/stop-dashboard`.
- Direct changes: no product source changes were needed; this dogfood entry records the task outcome and verification evidence.
- Blockers/friction: follow-up shell probes after verification intermittently failed with `windows sandbox: spawn setup refresh`, matching earlier local friction. The successful verification command completed before that issue recurred.
- Verification: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~DashboardHostTests.PrototypeDashboardServesHealthAndGoalJsonOverKestrel' --logger "trx;LogFileName=simple-hosted-dashboard-task-74896bbf.trx"` completed with exit code `0`.
- Next product follow-up: none for the requested simple hosted-dashboard behavior; keep monitoring the local sandbox spawn-refresh issue because it can block post-verification evidence collection.

## 2026-06-04 - Simple hosted dashboard goal 6b963f57

- Goal/task: active dashboard task `6b963f57eae0413ab1ed0ecc0613f857` (`Simple hosted dashboard goal`), assigned to Developer.
- What worked: repository survey and existing dogfood evidence both pointed to the hosted dashboard path. `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DashboardHostTests.cs` already verifies the simple hosted dashboard flow by launching the prototype dashboard, creating `Simple hosted dashboard goal` through `/api/goals`, confirming the created goal has one Developer task, reading goal detail, fetching `/api/source-survey`, fetching dashboard CSS/JS, checking continuation/process/build-test endpoints, and stopping the dashboard through `/api/system/stop-dashboard`.
- Direct changes: no product source changes were needed; this dogfood entry records the active task and verification evidence.
- Blockers/friction: this environment is not currently a Git worktree from `C:\Users\miles\vcs\mcg-agent-orchestrator`, so `git status --short` failed with `fatal: not a git repository`. Follow-up PowerShell launches for extra source-survey and TRX inspection failed with `windows sandbox: spawn setup refresh`.
- Verification: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~DashboardHostTests.PrototypeDashboardServesHealthAndGoalJsonOverKestrel' --logger "trx;LogFileName=simple-hosted-dashboard-task-6b963f57.trx"` completed with exit code `0`.
- Next product follow-up: none for the simple hosted dashboard behavior; the remaining friction is environment/process-launch reliability during dogfood verification.

## 2026-06-04 - Simple hosted dashboard goal 6fc2e263

- Goal/task: dashboard goal `Simple hosted dashboard goal`, Developer task `6fc2e26353d54f0184bdc9862f98b3ab`.
- What worked: focused survey of `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DashboardHostTests.cs` and dashboard API/rendering files showed the requested behavior is already implemented. The hosted dashboard test starts `prototype-ui`, creates `Simple hosted dashboard goal` through `/api/goals` with `workflow:"simple"`, verifies the created goal has one Developer task, confirms `/api/goals/{id}` includes verification status, checks `/api/source-survey`, assets, process diagnostics, cleanup metadata, continuation JSON, invalid request handling, and stops the dashboard through `/api/system/stop-dashboard`.
- Direct changes: no product source changes were required; this dogfood entry records the task-specific verification.
- Blockers/friction: this workspace does not expose `.git`, so `git status --short` could not be used. Some fresh PowerShell launches intermittently failed with `windows sandbox: spawn setup refresh`, but the focused test command and TRX inspection succeeded.
- Verification: `dotnet test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DashboardHostTests" --no-restore --logger "trx;LogFileName=simple-hosted-dashboard-task-6fc2e263.trx" --results-directory tests\Mcg.AgentOrchestrator.Infrastructure.Tests\TestResults` exited `0`. TRX `tests\Mcg.AgentOrchestrator.Infrastructure.Tests\TestResults\simple-hosted-dashboard-task-6fc2e263.trx` records `total="2" executed="2" passed="2" failed="0"` including `Prototype_dashboard_serves_health_and_goal_json_over_kestrel`.
- Next product follow-up: none for this task.
