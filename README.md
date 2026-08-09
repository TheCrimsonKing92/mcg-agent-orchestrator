# MCG Agent Orchestrator

Windows-oriented agent orchestrator for software development goals.

This repository contains the orchestration kernel: create a goal, decompose it into SDLC tasks, delegate each task to a role-matched agent, monitor dispatch and verification, and accept the result into `main`. The core deliberately does not automate browser sessions or consumer chat subscriptions. Existing subscriptions can be used through approved local CLI bridges such as the default Codex CLI and Claude CLI worker profiles.

## Projects

```text
src/Mcg.AgentOrchestrator.Core      Domain model and orchestration kernel
src/Mcg.AgentOrchestrator.Infrastructure  Providers, local processes, worker profiles
src/Mcg.AgentOrchestrator.App       Windows-friendly console host
mcg-orchestrator.cmd               Checkout-local Windows launcher
scripts/publish-windows.ps1        Windows publish helper for a local EXE
tests/Mcg.AgentOrchestrator.Core.Tests  Offline test harness
tests/Mcg.AgentOrchestrator.Infrastructure.Tests  Infrastructure test harness
```

## Verify

```powershell
dotnet build Mcg.AgentOrchestrator.sln -c Release
dotnet run --no-build --project tests\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.csproj -c Release
dotnet run --no-build --project tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -c Release
dotnet run --no-build --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -c Release -- prototype
```

## Windows Setup

From a checkout, use the launcher when you do not want to repeat the project path:

```cmd
mcg-orchestrator.cmd doctor
mcg-orchestrator.cmd goal "Build feature X"
mcg-orchestrator.cmd open-dashboard http://localhost:5087/ --refresh 5
```

To publish a local Windows executable:

```powershell
.\scripts\publish-windows.ps1
.\artifacts\mcg-agent-orchestrator-win-x64\Mcg.AgentOrchestrator.App.exe doctor
```

The default publish is framework-dependent and single-file for `win-x64`. Add `-SelfContained` when you need a larger build that does not rely on a locally installed .NET runtime.

Orchestrator-managed `dotnet test` runs write build/test artifacts under a bounded slot pool at `%TEMP%\mcg-dotnet-isolated\slots\` so Windows Firewall sees stable `testhost.exe` paths. If Windows prompts on first use, allow the `.NET testhost` path once; subsequent worker, verification, and acceptance runs reuse the same slot paths instead of creating per-goal executable locations.

---

# FUNDAMENTALS

The CLI centers on six alias verbs. All other verbs are covered in [ADVANCED](#advanced).

> **Operating the orchestrator? See [`docs/operator-runbook.md`](docs/operator-runbook.md).** The default path is the autonomous conductor (`conduct --loop`), which owns workspace creation, dispatch, acceptance, and landing. The "Core Loop" immediately below is the lower-level **manual** verb sequence — keep it for granular or fallback control, not as the primary workflow.

## Autonomous execution

Use `conduct --loop` as the main execution primitive. The conductor advances active goals through workspace creation, role dispatch, verification, acceptance, landing, dogfood logging, and cleanup until every goal is done or escalated.

Full SDLC goals use the five-role pipeline: Planner -> Researcher -> Developer -> Tester -> Reviewer. Intake and policy gates route engagement by risk, so trivial slices can use a minimal path while broader or riskier work gets the roles and human attention it needs.

The acceptance gate remains the safety boundary: a goal lands only after its task gates, configured build/test verification, and change-risk checks pass. For operating details, policies, stuck-goal recovery, and manual fallback commands, use [`docs/operator-runbook.md`](docs/operator-runbook.md).

## Core Loop (manual verbs — fallback; prefer `conduct --loop`)

```text
simple-goal "Build feature X"          # create a single-Developer goal
  (or: goal "Build feature X" --simple)
  (or: goal --brief-file brief.md)
subscription-dispatch 1                # prepare dispatch for the first task
start-dispatch 1 --confirm-dispatch-start
refresh-dispatch 1                     # collect result once the worker exits
accept                                 # merge to main when all tasks pass
```

Use `next` at any point to see the prioritized recommended next command for where the goal stands. Use `next --full` for a complete inspection view.

## `next` / `next --full`

```text
next [goal-id]
next [goal-id] --full
```

`next` prints prioritized recommended follow-up commands covering pending human input, failed tasks, failed verification, running dispatches, missing verification, assigned work, and pending tasks.

`next --full` expands the view with the full goal inspection: status, monitor, readiness preflight, evidence summary, stage readiness, verification gates, verification and human-input worklists, subscription plan, model outcomes, loop health, failure triage, recovery plan, supervisor plan, and operator inbox.

## `goal`

```text
goal <objective>
goal --brief-file <path>
goal <objective> --simple
goal --brief-file <path> --simple
```

`goal <objective>` creates a five-role goal (Planner → Researcher → Developer → Tester → Reviewer) and activates it for delegation. Pass `--brief-file <path>` to read the objective from a file instead of the command line — useful for long multi-line briefs.

Pass `--simple` to create a single-Developer goal (equivalent to `simple-goal`). `simple-goal <objective>` and `simple-goal --brief-file <path>` are the direct single-task forms.

## `accept`

```text
accept [goal-id] [--skip-verify] [--keep-workspace] [--autonomy <policy>]
```

Runs the goal acceptance check, fast-forwards the goal branch into `main`, auto-records a dogfood log entry, and removes the goal worktree. Stops before any gate that has not passed. Do not use `--skip-verify` to land an otherwise green goal; if a gate defect blocks landing, file and fix the gate bug, then re-run acceptance. Add `--keep-workspace` to skip worktree removal and `--autonomy` to override the default policy.

## `stop`

```text
stop <goal-id-prefix> <reason> --as cancel|park|rollback|abandon|supersede
```

Stops a goal using the named disposal mode:

- `cancel` — mark the goal stopped
- `supersede` — mark the goal stopped with replacement implied
- `park` — pause the goal for later resumption
- `rollback` — revert the goal branch to its pre-work state
- `abandon` — remove all goal artifacts including the worktree

## `config`

```text
config agents
config profiles
config policy
config doctor
```

Displays read-only configuration state. `agents` lists role-to-model assignments; `profiles` lists saved worker command templates; `policy` lists autonomy policy presets; `doctor` runs the setup health check (equivalent to the standalone `doctor` verb).

## `dashboard`

```text
dashboard [path] [--refresh seconds]
dashboard --mode local|hosted|read-only [url] [--refresh seconds]
```

Without `--mode`, writes a static HTML dashboard to `.orchestrator/dashboard.html` (or the specified path). Add `--refresh 10` to embed a browser auto-refresh interval. With `--mode local` or `--mode hosted`, hosts the live interactive dashboard on a local HTTP endpoint. Use `--mode read-only` for a browsable read-only view. See [Dashboard & SSE Monitoring](#dashboard--sse-monitoring) for the full hosted dashboard, browser scripting, and JSON API endpoints.

---

# ADVANCED

## Interactive Console

```powershell
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj
.\mcg-orchestrator.cmd
```

The same verbs run as one-shot CLI commands:

```powershell
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- goal "Build feature X"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- doctor
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- provider-smoke openai --confirm-paid-smoke 4
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- dashboard
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- serve-dashboard http://localhost:5087/ --refresh 5
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- open-dashboard
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- agents
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- agent Reviewer OpenAI gpt-5.2 "OpenAI reviewer"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- status
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- next
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- subscription-dispatch 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- start-dispatch 3 --confirm-dispatch-start
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- refresh-dispatch 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- accept
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- tasks status Assigned role Developer
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- brief 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- verify 4 "dotnet --version"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- verify-manual 4 passed "Manual smoke looked correct"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- pending
```

## Full Verb Surface

```text
doctor
provider-smoke [openai|anthropic|ollama] [--confirm-paid-smoke] [task-number]
prototype-ui [url] [--refresh seconds] [--no-open]
dashboard [path] [--refresh seconds]
serve-dashboard [url] [--refresh seconds]
open-dashboard [url] [--refresh seconds] [--no-open]
transcript [path]
goal <objective>
goals
agents
agent <role> <provider> <model> [name]
status
status [goal-id]
monitor [goal-id]
acceptance [goal-id]
evidence [goal-id]
stages [goal-id]
gates [goal-id]
verify-needed [goal-id]
input-needed [goal-id]
next [goal-id]
subscription-plan [goal-id]
advance [goal-id]
advance-subscription [goal-id] --confirm-subscription-advance [--confirm-large-paid-subscription-start]
delegate [goal-id]
task <task-number|task-id-prefix>
tasks [status <status>] [role <role>] [id <task-id-prefix>] [evidence <kind>] [event <kind>]
add-task <role> <description>
verification-plan <task-number> [plan]
brief <task-number>
timeline
timeline [goal-id]
task-timeline <task-number>
pending
run <task-number> [--confirm-paid-api-run] [--confirm-large-paid-api-prompt]
api-run <task-number> [--confirm-paid-api-run] [--confirm-large-paid-api-prompt]
retry <task-number> <message>
dispatch <task-number> <worker-name> <command>
worker-profiles
worker-profile <name> <command-template>
worker-profile-check [name]
worker-profile-export <path>
worker-profile-import <path> [merge|replace]
worker-dispatch <task-number> <worker-name> <command-template>
profile-dispatch <task-number> <profile-name>
profile-dispatch-ready <profile-name>
subscription-dispatch <task-number>
subscription-dispatch-ready
start-subscription-ready --confirm-batch-start [--confirm-large-paid-subscription-start]
execute-dispatch <task-number> --confirm-dispatch-start [--confirm-large-paid-subscription-start]
start-dispatch <task-number> --confirm-dispatch-start [--confirm-large-paid-subscription-start]
start-dispatches --confirm-batch-start [--confirm-large-paid-subscription-start]
refresh-dispatch <task-number>
refresh-dispatches
logs <task-number> [stdout|stderr|exit|all]
cancel-dispatch <task-number>
verify <task-number> <command>
verify-manual <task-number> <passed|failed> <note>
verifications <task-number>
progress <task-number> <running|completed|failed|cancelled> <message>
ask <task-number> <question>
ask-goal <question>
answer <request-id> <answer>
model-functions
model-function-add <purpose> <lane> <provider> <model> [name]
accept [goal-id] [--skip-verify] [--keep-workspace] [--autonomy <policy>]
stop <goal-id-prefix> <reason> --as cancel|park|rollback|abandon|supersede
config <agents|profiles|policy|doctor>
conduct <goal-id-prefix> [--policy <Conservative|Permissive|Manual>]
exit
```

`task` accepts either a display number or a task id prefix. `tasks` lists the current goal's tasks and can filter by `status`, `role`, task `id` prefix, evidence kind, and timeline event kind. Evidence kinds include `none`, `execution`, `dispatch`, `process`, `running-process`, `completed-process`, `verification`, `passed-verification`, and `failed-verification`. Timeline event kinds match `ProgressKind` names such as `TaskDispatchRecorded` or `TaskVerificationRecorded`. `add-task` appends a custom task to the current goal and delegates it immediately when a matching available agent role exists. Valid roles are `Planner`, `Researcher`, `Developer`, `Tester`, and `Reviewer`. `verification-plan <task-number>` prints the current task plan; add plan text to update the pre-work verification checklist included in task details, prompts, transcripts, and the dashboard. Use `task-timeline` to inspect only the events for one task.

`advance` executes the top-priority next action only when it is safe and fully specified, such as starting or refreshing a recorded dispatch or delegating pending work; it stops before API-backed model execution so `run <task-number>` or `api-run <task-number>` remains an explicit operator choice. `advance-subscription` follows the same safety policy, but prepares a provider-mapped subscription worker dispatch instead of directly running an assigned OpenAI or Anthropic task; add `--confirm-subscription-advance` when deliberately using that path, and add `--confirm-large-paid-subscription-start` when the selected paid subscription prompt is large enough to require explicit cost confirmation. `delegate` reruns role-based assignment for pending tasks.

`retry <task-number> <message>` reopens a failed, cancelled, or rework-needed task. The message is required so clearing execution or verification evidence has an explicit rework reason. Retry preserves prior execution and verification history, clears the latest verification gate, and moves assigned tasks back to `Assigned` so they can be run or dispatched again. Running tasks must be refreshed or cancelled before retrying, and tasks waiting for human input must be answered first.

`acceptance` reports whether the goal is accepted, how many task gates have passed, pending human input count, open verification count, and concrete blockers with suggested commands. `evidence` rolls up execution, dispatch, process, verification, and pending-human-input evidence across every task so the goal can be audited from one command. `stages` maps each SDLC task to a readiness state such as `ReadyToRun`, `InProgress`, `NeedsVerification`, `VerificationFailed`, or `Verified`, with the next command for that stage. `gates` reports whether each task is accepted for completion. `verify-needed` lists only open verification work and prints a suggested command for each task that is not ready, missing verification, or has failed verification. A task gate passes only when the task is completed and its latest verification succeeded. A goal is marked `Completed` only after every task gate passes.

`ask <task-number> <question>` opens a task-scoped human input request. `ask-goal <question>` opens a goal-scoped request when the orchestrator needs clarification that is not tied to one task. `input-needed` lists only pending human input for the current or selected goal, including task context when available, and prints the `answer <request-id> <answer>` command for each open request. `pending` keeps the broader all-goals pending-input view. Model-backed agents, foreground dispatches, and refreshed background dispatches can request operator input by emitting a line that starts with `HUMAN_INPUT:` followed by the exact question; the task and goal pause until the request is answered.

## Dispatch Internals

`dispatch` records work intent and marks the task running. `execute-dispatch <task-number> --confirm-dispatch-start` runs the latest dispatch command synchronously from the recorded working directory, records stdout/stderr/exit code, and marks the task completed on exit code `0` or failed otherwise, unless stdout or stderr includes `HUMAN_INPUT:` and pauses the task for operator input. Add `--confirm-large-paid-subscription-start` when the recorded paid subscription dispatch has a large prompt.

`brief` prints a role-specific prompt for the selected task, including the verification plan and the `HUMAN_INPUT:` directive agents should use when they cannot proceed without operator input. `worker-dispatch` writes that brief under `.orchestrator/prompts` and expands a command template. Supported placeholders are `{promptPath}`, `{goalId}`, `{taskId}`, `{role}`, and `{title}`. This is the generic bridge for local coding-agent CLIs; configure the template for the tool you actually use.

`worker-profile` saves a reusable template by name. `worker-profile-check [name]` validates that saved profile commands are locally resolvable. `worker-profile-export <path>` writes the current profile catalog to a JSON file. `worker-profile-import <path> [merge|replace]` imports another catalog, merging by default or replacing the current catalog when requested. `profile-dispatch` uses a saved profile to create a prompt file and dispatch command. `profile-dispatch-ready` creates dispatches for all currently assigned tasks using one saved profile and reports the generated prompt paths. `subscription-plan` shows each task's effective complexity/model choice, how it maps to `codex-cli` or `claude-cli`, whether the profile exists, whether the executable resolves locally, and whether the task is ready to prepare. `subscription-dispatch` chooses `codex-cli` for assigned OpenAI agents and `claude-cli` for assigned Anthropic agents, passing the selected subscription model explicitly to avoid external CLI defaults. `subscription-dispatch-ready` applies that mapping to all assigned tasks. `start-subscription-ready --confirm-batch-start` combines subscription dispatch preparation with starting every eligible background worker process; add `--confirm-large-paid-subscription-start` when the prepared paid prompt batch is large. The default profile is `local-echo`.

`start-dispatch <task-number> --confirm-dispatch-start` starts the latest dispatch command in a hidden PowerShell process, writes stdout/stderr/exit code under `.orchestrator/logs`, and records the process id. `start-dispatches --confirm-batch-start` starts every running task that has a recorded dispatch and no currently running process, allowing local workers to run concurrently, and reports ready/skipped task reasons. Add `--confirm-large-paid-subscription-start` when starting a large paid prepared dispatch. `refresh-dispatch` checks whether one process has exited; `refresh-dispatches` checks every running background process and reports ready/skipped task reasons. Completed background dispatches record the same verification evidence and task completion/failure as foreground execution, including `HUMAN_INPUT:` pause detection from captured stdout or stderr. Use `logs <task-number> [stdout|stderr|exit|all]` to inspect captured process output through the orchestrator.

`cancel-dispatch` terminates the tracked background process tree and marks the task cancelled.

`verify` runs a local command and appends the result to the task history. `verify-manual` appends human-observed pass/fail evidence without running a command. Both update the latest verification shown in `task`, `monitor`, task briefs, and the dashboard. Use `verifications <task-number>` to inspect the retained evidence trail.

## Agent Configuration

`agents` lists the role-to-model assignments used for delegation and `run`. `agent <role> <provider> <model> [name]` replaces the agent for one SDLC role and saves the catalog to `.orchestrator/agents.json`.

Example:

```powershell
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- agent Developer Anthropic claude-sonnet-4-6 "Claude developer"
```

Valid roles are `Planner`, `Researcher`, `Developer`, `Tester`, and `Reviewer`. Providers must match a registered model provider such as `OpenAI`, `Anthropic`, or `Ollama` for API-backed execution to succeed.

## Model Providers

Without credentials, API-backed execution uses deterministic offline providers named `OpenAI` and `Anthropic`. This keeps orchestration behavior verifiable without network access. Use `subscription-dispatch <task-number>` for subscription-capable agents first; `api-run <task-number>` is the explicit API-backed fallback and refuses subscription-capable tasks once subscription work, model output, or verification evidence exists. Add `--confirm-paid-api-run` in the CLI, or `confirmPaidApiRun=true` in dashboard API calls, when intentionally running an OpenAI or Anthropic task through the API. Large paid API prompts also require `--confirm-large-paid-api-prompt` in the CLI or `confirmLargePaidApiPrompt=true` in dashboard API calls. Local Ollama execution does not require those flags.

Set these environment variables to use live API providers:

```powershell
$env:OPENAI_API_KEY = "<api-key>"
$env:OPENAI_MODEL = "gpt-5.2"
$env:ANTHROPIC_API_KEY = "<api-key>"
$env:ANTHROPIC_MODEL = "claude-sonnet-4-6"
```

The OpenAI adapter calls the Responses API. The Anthropic adapter calls the Messages API. Both adapters are covered by offline fake-HTTP tests for request shape, authentication headers, response parsing, and HTTP error handling.

### Ollama (Local Models)

The Ollama provider auto-detects a running Ollama server at startup — no API key required. Install [Ollama](https://ollama.com), pull a model, and the orchestrator picks it up automatically.

```powershell
winget install Ollama.Ollama
ollama pull qwen3:8b
```

Override the defaults with environment variables:

```powershell
$env:OLLAMA_BASE_URL = "http://localhost:11434"   # default
$env:OLLAMA_MODEL = "qwen3:8b"                    # default
```

The Ollama adapter uses the OpenAI-compatible chat completions API, so it also works with other local inference servers (vLLM, LM Studio) that expose the same endpoint. Thinking-model output (e.g. Qwen3 reasoning) is captured as fallback content when the model exhausts its token budget before producing a final answer.

### Smoke Testing

Use `provider-smoke [openai|anthropic|ollama]` after configuring one provider to make a live, minimal request and print the provider response, stop reason, and token usage. Omitting the provider tests reachable local Ollama only and does not fall back to paid providers. Use `provider-smoke openai --confirm-paid-smoke` or `provider-smoke anthropic --confirm-paid-smoke` only when deliberately making a paid smoke request. The command skips unconfigured providers and fails if no selected live provider is available. Add a task number, for example `provider-smoke openai --confirm-paid-smoke 4`, to append the successful smoke result to that task's verification history. Use `provider-smoke all --confirm-all` only when intentionally comparing every configured provider.

## Setup Doctor

`doctor` reports:

- whether `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, and Ollama are configured
- whether the app is using live API mode or offline scripted provider mode
- whether every SDLC role has an agent and each agent references a registered provider
- whether saved worker profile commands are resolvable locally through PowerShell

`doctor` does not make network calls. Use `provider-smoke` when you want to prove live model connectivity.

## Semantic Acceptance Judges / Model Functions

`model-functions` lists the orchestrator's internal model function bindings — models the orchestrator invokes for its own functions such as acceptance judges, not worker agents assigned to goal tasks. `model-function-add <purpose> <lane> <provider> <model> [name]` registers a new binding. Unlike agent role assignments, model functions use an open `purpose` string and are stored under `.orchestrator/model-functions.json`. They compose with deterministic gates — LLM judgment results land advisory and never replace a deterministic acceptance gate.

## Dashboard & SSE Monitoring

`monitor [goal-id]` reports task status counts, pending human input, last timeline event time, and attention items for:

- pending human input
- failed tasks
- failed verification results
- running dispatches
- completed tasks that do not yet have verification evidence

`dashboard [path] [--refresh seconds]` writes an HTML dashboard. If no path is provided, it writes `.orchestrator/dashboard.html`. Add `--refresh 10` when you want the opened browser tab to reload itself while workers are running. The dashboard includes status counts, attention items, the goal acceptance summary, the goal evidence summary, SDLC stage readiness, row-level task gate status, latest task evidence, verification gates, the open human-input and verification worklists, and the same prioritized next-action suggestions as the `next` command.

`prototype-ui [url] [--refresh seconds] [--no-open]` serves the hosted dashboard from a persistent prototype state workspace under `src/Mcg.AgentOrchestrator.App\.orchestrator-prototype\workspace`, seeds sample state only when needed, keeps `local-echo` as the explicit harmless echo profile, and configures model-pinned subscription worker profiles for real local CLI execution (`codex-cli` and `claude-cli`). Existing persisted legacy echo or unpinned subscription profiles are repaired on startup. When launched through `mcg-orchestrator.cmd`, task execution runs from the repository root while state remains isolated from the real `.orchestrator/state.db`. Use `--no-open` for scripts or smoke tests that should host the dashboard without launching a browser.

`serve-dashboard [url] [--refresh seconds]` hosts the same dashboard from current state on a local HTTP endpoint, defaulting to `http://localhost:5087/` with a five-second browser refresh. The hosted dashboard includes setup doctor status, agent role/model and worker-profile configuration controls, direct controls for safe next actions, and browser controls for creating goals, adding custom SDLC tasks, updating verification plans, delegating work, running assigned model-backed tasks, advancing with subscription-backed dispatches, preparing worker dispatches, preparing subscription-backed worker dispatches, reporting task progress, asking goal-scoped and task-scoped human questions, starting and refreshing batch dispatches, answering pending human input, running verification commands, and recording manual pass/fail verification. When continuation stops only because background subscription work is still running, the host starts a server-side continuation watch that refreshes the process and resumes handoff after it exits; `/api/continuations` and the operator panel show watcher status. The host reloads `.orchestrator/state.db` on each request, so the page reflects changes made by other CLI commands while it is open. Press Ctrl+C in the hosting console to stop it.

`open-dashboard [url] [--refresh seconds] [--no-open]` uses the same host as `serve-dashboard` and opens the dashboard URL in the default Windows browser. Use `--no-open` for scripts or smoke tests that should host the dashboard without launching a browser.

For repeatable headless dashboard validation, start the prototype dashboard and run the checked-in Edge DevTools harness:

```powershell
.\mcg-orchestrator.cmd prototype-ui http://localhost:5087/ --refresh 5 --no-open
.\scripts\Run-DashboardBrowserScript.ps1 .\scripts\dashboard-smoke.js
```

The smoke script checks the rendered operator page, source survey link, goal-detail link, and build/test stop control without stopping the dashboard. Custom dashboard validation scripts can be placed anywhere under the repository and run through the same wrapper.

For routine dogfood actions through the dashboard, use the parameterized helper instead of creating one-off scratch JavaScript:

```powershell
.\scripts\Invoke-DashboardDogfoodAction.ps1 -Action create-goal -Objective "Dogfood: validate a dashboard workflow" -Workflow simple
.\scripts\Invoke-DashboardDogfoodAction.ps1 -Action complete-task -Goal <goal-id-or-prefix> -TaskNumber 1 -Note "Verified through dashboard and tests."
.\scripts\Invoke-DashboardDogfoodAction.ps1 -Action smoke
```

For a full Windows build/test cycle while the dashboard is running, use the coordinated helper:

```powershell
.\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl http://localhost:5087/
```

The helper consumes `/api/system/build-test-cleanup`, stops only exact dashboard PIDs listed by the dashboard plan, runs `dotnet build` and `dotnet test` sequentially, restarts `prototype-ui`, and waits for `/health`.

The dashboard host exposes local JSON endpoints for scripted monitoring and future UI surfaces:

- `/health`
- `/api/health` or `/api/doctor`
- `/api/agents`
- `POST /api/agents` with JSON like `{ "role": "Developer", "providerName": "OpenAI", "modelName": "gpt-5-codex", "name": "OpenAI developer" }`
- `/api/worker-profiles` returns saved worker profiles plus `executable`, `isResolvable`, `isOptional`, and `detail` availability fields
- `POST /api/worker-profiles` with JSON like `{ "name": "codex", "commandTemplate": "codex exec {promptPath}" }`
- `/api/goals`
- `POST /api/goals` with either a raw objective body or JSON like `{ "objective": "Build feature X" }`; include `{ "autoHandoff": true, "confirmAutoHandoff": true }` only when deliberately starting subscription handoff during goal creation
- `/api/monitor?goal=<goal-id-prefix>`
- `/api/acceptance?goal=<goal-id-prefix>` returns the goal acceptance summary, blocker list, and suggested commands
- `/api/evidence?goal=<goal-id-prefix>` returns the goal evidence summary with per-task latest evidence and rollup counts
- `/api/stages?goal=<goal-id-prefix>` returns SDLC stage readiness with per-stage status, evidence, verification gate, suggested action, and suggested command
- `/api/next?goal=<goal-id-prefix>` returns prioritized next actions, suggested CLI commands, and optional `Control` metadata with `Label`, `Method`, and `Url` for safe direct actions
- `/api/gates?goal=<goal-id-prefix>`
- `/api/verification-worklist?goal=<goal-id-prefix>` returns only open verification work with suggested actions and commands
- `/api/human-input-worklist?goal=<goal-id-prefix>` returns only pending human input with task context, suggested actions, and suggested answer commands
- `/api/pending-input?goal=<goal-id-prefix>`
- `POST /api/input/<request-id-prefix>/answer` with either a raw text body or JSON like `{ "answer": "Use main." }`
- `/api/tasks?goal=<goal-id-prefix>&status=<status>&role=<role>&evidence=<kind>&event=<kind>`
- `/api/task/<task-number-or-id-prefix>?goal=<goal-id-prefix>`
- `POST /api/goals/<goal-id-prefix>/tasks` with JSON like `{ "role": "Developer", "description": "Implement retry handling", "delegate": true, "verificationPlan": "Run dotnet build." }`
- `/api/goals/<goal-id-prefix>/transcript`
- `/api/goals/<goal-id-prefix>/subscription-plan`
- `POST /api/goals/<goal-id-prefix>/ask` with either a raw question body or JSON like `{ "question": "Which repository should this goal target?" }`
- `POST /api/goals/<goal-id-prefix>/advance`
- `POST /api/goals/<goal-id-prefix>/advance-subscription?confirmSubscriptionAdvance=true`
- `POST /api/goals/<goal-id-prefix>/advance-subscription-until-blocked?confirmSubscriptionAdvance=true`
- `POST /api/goals/<goal-id-prefix>/delegate`
- `POST /api/goals/<goal-id-prefix>/profile-dispatch-ready` with either a raw profile name body or JSON like `{ "profileName": "local-echo" }`; the response includes `Dispatches` entries with `Task`, generated `PromptPath`, and `LastDispatch` values
- `POST /api/goals/<goal-id-prefix>/subscription-dispatch-ready`; the response includes provider-selected `Dispatches` entries with `Task`, generated `PromptPath`, and `LastDispatch` values
- `POST /api/goals/<goal-id-prefix>/start-subscription-ready?confirmBatchStart=true`; add `confirmLargePaidSubscriptionStart=true` when starting a large paid prepared subscription batch. The response includes provider-selected `Dispatches`, `ProcessPlan` ready/skipped reasons, and `Processes` for started tasks
- `POST /api/goals/<goal-id-prefix>/start-dispatches?confirmBatchStart=true`; add `confirmLargePaidSubscriptionStart=true` when starting a large paid prepared dispatch. The response includes `ProcessPlan` ready/skipped reasons and `Processes` for changed tasks
- `POST /api/goals/<goal-id-prefix>/refresh-dispatches`; the response includes `ProcessPlan` ready/skipped reasons and `Processes` for changed tasks
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/run?confirmTaskRun=true`; add `confirmPaidApiRun=true` when this will run OpenAI or Anthropic through the API, and add `confirmLargePaidApiPrompt=true` when the exact prompt preview reports a large paid prompt
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/api-run?confirmTaskRun=true`; add `confirmPaidApiRun=true` when this will run OpenAI or Anthropic through the API, and add `confirmLargePaidApiPrompt=true` when the exact prompt preview reports a large paid prompt
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/retry` with either a raw note body or JSON like `{ "message": "Retry after failed verification" }`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/dispatch` with JSON like `{ "workerName": "local", "command": "Write-Output ok" }`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/profile-dispatch` with either a raw profile name body or JSON like `{ "profileName": "codex-cli" }`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/subscription-dispatch`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/start?confirmDispatchStart=true`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/refresh`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/cancel`
- `/api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/brief`
- `/api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/timeline`
- `/api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/gate`
- `/api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/verification-plan`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/verification-plan` with either a raw plan body or JSON like `{ "plan": "Run dotnet test." }`
- `/api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/verifications`
- `/api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/logs`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/progress` with JSON like `{ "status": "completed", "message": "Implementation finished" }`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/ask` with either a raw question body or JSON like `{ "question": "Which branch should I use?" }`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/verify` with either a raw command body or JSON like `{ "command": "dotnet --version" }`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/verify-manual` with JSON like `{ "passed": true, "note": "Manual smoke passed" }`

`transcript [path]` writes a Markdown snapshot of the current goal. If no path is provided, it writes `.orchestrator/transcript.md`. The transcript includes monitor status, next actions, attention items, the goal acceptance summary, the goal evidence summary, SDLC stage readiness, the open human-input worklist, verification gates, the open verification worklist, task verification plans, task evidence, verification history, task timelines, and the goal timeline.

## Autonomous Conductor

The conductor drives a goal through its full lifecycle in discrete, policy-gated steps. Each call to `conduct` advances the goal exactly once; the operator can loop it externally or call it from a script.

```text
conduct <goal-id-prefix> [--policy <Conservative|Permissive|Manual>]
```

### Autonomous execution

For a full SDLC goal, the conductor advances the worker chain from Planner → Researcher → Developer → Tester → Reviewer.
Workers make edits only in the goal worktree; the orchestrator owns committing verified worker edits.
After review, the acceptance gate runs the configured build and test suite against that worktree.
If the goal is clean and low risk under the active policy, the conductor auto-promotes it by merging to `main` without human sign-off.
Risky landings, failed gates, conflicts, human-input states, or repeatedly troubled attempts escalate to the operator for review instead of landing automatically.

### Lifecycle states

The conductor follows a deterministic state machine:

```
Created → WorkspaceReady → Dispatched → Running → AwaitingVerification
        → Verified → Merged → Recorded → CleanedUp
```

Error states — `Failed`, `Blocked`, and `AwaitingHumanInput` — always escalate to the operator inbox regardless of policy and require human action before the conductor can resume.

At each state the conductor decides `Auto` (proceed) or `Escalate` (pause and page the operator) based on the active policy. Held states (`Dispatched`, `Running`, `AwaitingVerification`) are also `Auto`: the conductor returns immediately and the operator should call `conduct` again after workers have had a chance to make progress.

### Autonomy policy presets

| Preset | Workers | Auto-promote threshold | Merging |
|---|---|---|---|
| `Conservative` *(default)* | 4 | DocsOnly changes only | Escalates for code/build changes |
| `Permissive` | 5 | All change types (DocsOnly → Broad) | Auto for all passing changes |
| `Manual` | 1 | None | Escalates at every step |

Conductor policy currently provides no cost ceiling because the shipped providers do not expose comparable, authoritative spend. Retired budget keys in older policy files are accepted and ignored.

**`Auto`** means the conductor proceeds without human sign-off. **`Escalate`** means the conductor writes a blocker to the operator inbox (and pages Discord) and returns without advancing the state.

The default policy is `Conservative`. Override per-invocation with `--policy`, or persist a custom policy in `.orchestrator/conductor-policy.json`.

### Integration-branch landing

When the goal reaches `Verified`, the conductor runs three gates before promoting to `main`:

1. **Acceptance verification** — runs the configured test/build suite against the goal worktree. A failing suite escalates immediately; no merge is attempted.
2. **Change-risk gate** — classifies the changed files (`DocsOnly`, `Behavior`, `Build`, `Security`, or `Broad`) and compares the risk against the policy's `AutoPromoteRiskThreshold`. If the change is riskier than the threshold, the conductor escalates rather than auto-promoting.
3. **Integration-branch merge** — `LandingExecutor` fast-forward merges the goal branch into `main` via the integration branch. If the engine decides `Escalate` (for example, because the branch cannot be fast-forwarded cleanly), the escalation reason is written to the operator inbox.

When all three gates pass the goal moves to `Merged`, then to `Recorded` (written to `.orchestrator/dogfood-log.db`), then to `CleanedUp` (worktree removed).

### Operator pager (Discord)

Escalations are routed through the configured `IOperatorChannel`. The built-in Discord channel (`DiscordOperatorChannel`) creates one forum thread per goal the first time an escalation fires for that goal, titled `[<goal-prefix>] Orchestrator Escalations`. Subsequent escalations for the same goal are posted as messages in the existing thread. Thread IDs are persisted in `.orchestrator/discord-threads.json` so they survive process restarts. Buttons attached to each message let the operator acknowledge or act without leaving Discord.

Enable the Discord pager by providing a forum channel ID and bot token; the local operator inbox (`operator-inbox`) remains the fallback when Discord is not configured.

## Next Slices

- Add a Windows UI or tray host once the headless behavior is stable.
