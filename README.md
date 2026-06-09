# MCG Agent Orchestrator

Windows-oriented agent orchestrator for software development goals.

This repository currently contains the first tested orchestration kernel:

- Create a software development goal.
- Decompose it into standard SDLC tasks.
- Add custom role-scoped tasks to an existing goal.
- Attach role-specific verification plans to tasks before work starts.
- Delegate tasks to role-matched agents.
- Retry delegation for pending tasks when agent availability changes.
- Configure role-to-model agent assignments without editing code.
- Track status and timeline events.
- Pause a goal/task for human input.
- Resume work when human input is submitted.
- Let model-backed agents and local worker CLIs request human input by emitting a `HUMAN_INPUT:` directive.
- Represent OpenAI, Anthropic, and Ollama models through provider-agnostic model profiles.
- Validate OpenAI and Anthropic HTTP adapter request/response behavior without network access.
- Persist local orchestration state in `.orchestrator/state.json`.
- Run an assigned task through the task's assigned model provider.
- Use offline scripted providers by default, or live OpenAI/Anthropic/Ollama HTTP providers when available.
- Smoke-test live OpenAI/Anthropic/Ollama connectivity explicitly, optionally recording the result as task verification evidence.
- Map assigned OpenAI and Anthropic agents to default local subscription worker profiles (`codex-cli` and `claude-cli`) when CLI bridges are available.
- Dispatch task instructions to a named worker for auditable human or agent follow-through.
- Prepare dispatches for all assigned tasks from one worker profile.
- Execute the latest dispatch for a task in the foreground and capture its result.
- Start the latest dispatch in a background process and refresh it later to collect logs and exit status.
- Start or refresh all running dispatches as a batch for parallel local worker execution, including ready/skipped task reasons.
- Inspect captured stdout, stderr, and exit-code logs from background dispatches.
- Cancel a running background dispatch and persist the cancellation state.
- Record local verification command results on a task, including exit code, stdout, stderr, and timestamp.
- Retain verification history per task while still showing the latest verification in summaries.
- Monitor task counts and attention items across a goal.
- Report verification gates, a goal acceptance summary, a goal evidence summary, SDLC stage readiness, and a focused verification worklist before accepting a goal as complete.
- Report a focused human input worklist for pending operator questions.
- Generate role-specific task briefs for local worker or coding-agent CLIs.
- Dispatch configurable worker command templates using generated prompt files.
- Store reusable worker command profiles under `.orchestrator/workers.json`.
- Store reusable agent role/model assignments under `.orchestrator/agents.json`.
- Validate model-provider credentials, agent/provider configuration, and worker command availability with a setup doctor.
- Export a static HTML dashboard for a lightweight visual monitoring surface with next-action suggestions.
- Launch the hosted dashboard in a browser for a Windows-friendly operator surface.
- Export a Markdown transcript for handoff, review, and audit.

The core deliberately does not automate browser sessions or consumer chat subscriptions. Existing subscriptions can be used through approved local CLI bridges such as the default Codex CLI and Claude CLI worker profiles.

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

## Windows Usage

From a checkout, use the launcher when you do not want to repeat the project path:

```cmd
mcg-orchestrator.cmd doctor
mcg-orchestrator.cmd goal "Build feature X"
mcg-orchestrator.cmd open-dashboard http://localhost:5087/ --refresh 5
mcg-orchestrator.cmd prototype-ui http://localhost:5087/ --refresh 5
```

To publish a local Windows executable:

```powershell
.\scripts\publish-windows.ps1
.\artifacts\mcg-agent-orchestrator-win-x64\Mcg.AgentOrchestrator.App.exe doctor
```

The default publish is framework-dependent and single-file for `win-x64`. Add `-SelfContained` when you need a larger build that does not rely on a locally installed .NET runtime.

## Interactive Console

```powershell
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj
.\mcg-orchestrator.cmd
```

Commands:

```text
doctor
provider-smoke [openai|anthropic|ollama] [task-number]
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
advance-subscription [goal-id]
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
run <task-number>
api-run <task-number>
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
start-subscription-ready
execute-dispatch <task-number>
start-dispatch <task-number>
start-dispatches
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
exit
```

The same commands can be run as one-shot CLI commands:

```powershell
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- goal "Build feature X"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- doctor
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- provider-smoke openai 4
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- prototype-ui http://localhost:5087/ --refresh 5
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- dashboard
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- dashboard .orchestrator\dashboard.html --refresh 10
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- serve-dashboard http://localhost:5087/ --refresh 5
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- open-dashboard
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- transcript
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- agents
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- agent Reviewer OpenAI gpt-5.2 "OpenAI reviewer"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- status
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- monitor
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- acceptance
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- evidence
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- stages
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- gates
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- verify-needed
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- input-needed
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- next
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- subscription-plan
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- advance
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- advance-subscription
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- delegate
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- ask-goal "Which repository should this goal target?"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- tasks status Assigned role Developer
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- tasks evidence failed-verification
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- tasks event TaskDispatchRecorded
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- add-task Developer "Implement retry handling"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- verification-plan 3 "Run dotnet build and focused tests before acceptance"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- task-timeline 6
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- brief 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- run 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- api-run 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- retry 3 "Retry after failed verification"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- dispatch 3 local "dotnet --version"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- worker-profiles
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- worker-profile echo-title "Write-Output {title}"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- worker-profile-check echo-title
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- worker-profile-export .orchestrator\workers.backup.json
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- worker-profile-import .orchestrator\workers.backup.json merge
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- worker-dispatch 3 local "Write-Output {promptPath}"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- profile-dispatch 3 echo-title
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- profile-dispatch-ready local-echo
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- subscription-dispatch 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- subscription-dispatch-ready
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- start-subscription-ready
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- execute-dispatch 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- start-dispatch 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- start-dispatches
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- refresh-dispatch 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- refresh-dispatches
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- logs 3 all
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- cancel-dispatch 3
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- verify 4 "dotnet --version"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- verify-manual 4 passed "Manual smoke looked correct"
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- verifications 4
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- pending
```

`task` accepts either a display number or a task id prefix. `tasks` lists the current goal's tasks and can filter by `status`, `role`, task `id` prefix, evidence kind, and timeline event kind. Evidence kinds include `none`, `execution`, `dispatch`, `process`, `running-process`, `completed-process`, `verification`, `passed-verification`, and `failed-verification`. Timeline event kinds match `ProgressKind` names such as `TaskDispatchRecorded` or `TaskVerificationRecorded`. `add-task` appends a custom task to the current goal and delegates it immediately when a matching available agent role exists. Valid roles are `Planner`, `Researcher`, `Developer`, `Tester`, and `Reviewer`. `verification-plan <task-number>` prints the current task plan; add plan text to update the pre-work verification checklist included in task details, prompts, transcripts, and the dashboard. Use `task-timeline` to inspect only the events for one task.

`next` prints prioritized recommended follow-up commands for pending human input, failed tasks, failed verification, running dispatches, missing verification, assigned work, and pending tasks. `advance` executes the top-priority next action only when it is safe and fully specified, such as starting or refreshing a recorded dispatch or delegating pending work; it stops before API-backed model execution so `run <task-number>` or `api-run <task-number>` remains an explicit operator choice. `advance-subscription` follows the same safety policy, but prepares a provider-mapped subscription worker dispatch instead of directly running an assigned OpenAI or Anthropic task. `delegate` reruns role-based assignment for pending tasks.

`retry <task-number> <message>` reopens a failed, cancelled, or rework-needed task. The message is required so clearing execution or verification evidence has an explicit rework reason. Retry preserves prior execution and verification history, clears the latest verification gate, and moves assigned tasks back to `Assigned` so they can be run or dispatched again. Running tasks must be refreshed or cancelled before retrying, and tasks waiting for human input must be answered first.

`acceptance` reports whether the goal is accepted, how many task gates have passed, pending human input count, open verification count, and concrete blockers with suggested commands. `evidence` rolls up execution, dispatch, process, verification, and pending-human-input evidence across every task so the goal can be audited from one command. `stages` maps each SDLC task to a readiness state such as `ReadyToRun`, `InProgress`, `NeedsVerification`, `VerificationFailed`, or `Verified`, with the next command for that stage. `gates` reports whether each task is accepted for completion. `verify-needed` lists only open verification work and prints a suggested command for each task that is not ready, missing verification, or has failed verification. A task gate passes only when the task is completed and its latest verification succeeded. A goal is marked `Completed` only after every task gate passes.

`ask <task-number> <question>` opens a task-scoped human input request. `ask-goal <question>` opens a goal-scoped request when the orchestrator needs clarification that is not tied to one task. `input-needed` lists only pending human input for the current or selected goal, including task context when available, and prints the `answer <request-id> <answer>` command for each open request. `pending` keeps the broader all-goals pending-input view. Model-backed agents, foreground dispatches, and refreshed background dispatches can request operator input by emitting a line that starts with `HUMAN_INPUT:` followed by the exact question; the task and goal pause until the request is answered.

`dispatch` records work intent and marks the task running. `execute-dispatch` runs the latest dispatch command synchronously from the recorded working directory, records stdout/stderr/exit code, and marks the task completed on exit code `0` or failed otherwise, unless stdout or stderr includes `HUMAN_INPUT:` and pauses the task for operator input.

`brief` prints a role-specific prompt for the selected task, including the verification plan and the `HUMAN_INPUT:` directive agents should use when they cannot proceed without operator input. `worker-dispatch` writes that brief under `.orchestrator/prompts` and expands a command template. Supported placeholders are `{promptPath}`, `{goalId}`, `{taskId}`, `{role}`, and `{title}`. This is the generic bridge for local coding-agent CLIs; configure the template for the tool you actually use.

`worker-profile` saves a reusable template by name. `worker-profile-check [name]` validates that saved profile commands are locally resolvable. `worker-profile-export <path>` writes the current profile catalog to a JSON file. `worker-profile-import <path> [merge|replace]` imports another catalog, merging by default or replacing the current catalog when requested. `profile-dispatch` uses a saved profile to create a prompt file and dispatch command. `profile-dispatch-ready` creates dispatches for all currently assigned tasks using one saved profile and reports the generated prompt paths. `subscription-plan` shows each task's effective complexity/model choice, how it maps to `codex-cli` or `claude-cli`, whether the profile exists, whether the executable resolves locally, and whether the task is ready to prepare. `subscription-dispatch` chooses `codex-cli` for assigned OpenAI agents and `claude-cli` for assigned Anthropic agents. `subscription-dispatch-ready` applies that mapping to all assigned tasks. `start-subscription-ready` combines subscription dispatch preparation with starting every eligible background worker process. The default profile is `local-echo`.

`start-dispatch` starts the latest dispatch command in a hidden PowerShell process, writes stdout/stderr/exit code under `.orchestrator/logs`, and records the process id. `start-dispatches` starts every running task that has a recorded dispatch and no currently running process, allowing local workers to run concurrently, and reports ready/skipped task reasons. `refresh-dispatch` checks whether one process has exited; `refresh-dispatches` checks every running background process and reports ready/skipped task reasons. Completed background dispatches record the same verification evidence and task completion/failure as foreground execution, including `HUMAN_INPUT:` pause detection from captured stdout or stderr. Use `logs <task-number> [stdout|stderr|exit|all]` to inspect captured process output through the orchestrator.

`cancel-dispatch` terminates the tracked background process tree and marks the task cancelled.

`verify` runs a local command and appends the result to the task history. `verify-manual` appends human-observed pass/fail evidence without running a command. Both update the latest verification shown in `task`, `monitor`, task briefs, and the dashboard. Use `verifications <task-number>` to inspect the retained evidence trail.

## Agent Configuration

`agents` lists the role-to-model assignments used for delegation and `run`. `agent <role> <provider> <model> [name]` replaces the agent for one SDLC role and saves the catalog to `.orchestrator/agents.json`.

Example:

```powershell
dotnet run --project src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj -- agent Developer Anthropic claude-sonnet-4-20250514 "Claude developer"
```

Valid roles are `Planner`, `Researcher`, `Developer`, `Tester`, and `Reviewer`. Providers must match a registered model provider such as `OpenAI`, `Anthropic`, or `Ollama` for API-backed execution to succeed.

## Model Providers

Without credentials, API-backed execution uses deterministic offline providers named `OpenAI` and `Anthropic`. This keeps orchestration behavior verifiable without network access. Use `subscription-dispatch <task-number>` for subscription-capable agents first; `api-run <task-number>` is the explicit API-backed fallback and refuses subscription-capable tasks once subscription work, model output, or verification evidence exists.

Set these environment variables to use live API providers:

```powershell
$env:OPENAI_API_KEY = "<api-key>"
$env:OPENAI_MODEL = "gpt-5.2"
$env:ANTHROPIC_API_KEY = "<api-key>"
$env:ANTHROPIC_MODEL = "claude-sonnet-4-20250514"
```

The OpenAI adapter calls the Responses API. The Anthropic adapter calls the Messages API.
Both adapters are covered by offline fake-HTTP tests for request shape, authentication headers, response parsing, and HTTP error handling.

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

Use `provider-smoke [openai|anthropic|ollama]` after configuring one provider to make a live, minimal request and print the provider response, stop reason, and token usage. Omitting the provider prefers reachable local Ollama and falls back to OpenAI. The command skips unconfigured providers and fails if no live provider is available. Add a task number, for example `provider-smoke openai 4`, to append the successful smoke result to that task's verification history. Use `provider-smoke all --confirm-all` only when intentionally comparing every configured provider.

## Setup Doctor

`doctor` reports:

- whether `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, and Ollama are configured
- whether the app is using live API mode or offline scripted provider mode
- whether every SDLC role has an agent and each agent references a registered provider
- whether saved worker profile commands are resolvable locally through PowerShell

`doctor` does not make network calls. Use `provider-smoke` when you want to prove live model connectivity.

## Monitoring

`monitor [goal-id]` reports task status counts, pending human input, last timeline event time, and attention items for:

- pending human input
- failed tasks
- failed verification results
- running dispatches
- completed tasks that do not yet have verification evidence

`dashboard [path] [--refresh seconds]` writes an HTML dashboard. If no path is provided, it writes `.orchestrator/dashboard.html`. Add `--refresh 10` when you want the opened browser tab to reload itself while workers are running. The dashboard includes status counts, attention items, the goal acceptance summary, the goal evidence summary, SDLC stage readiness, row-level task gate status, latest task evidence, verification gates, the open human-input and verification worklists, and the same prioritized next-action suggestions as the `next` command.

`prototype-ui [url] [--refresh seconds] [--no-open]` serves the hosted dashboard from a persistent prototype state workspace under `src/Mcg.AgentOrchestrator.App\.orchestrator-prototype\workspace`, seeds sample state only when needed, keeps `local-echo` as the explicit harmless echo profile, and configures subscription worker profiles for real local CLI execution (`codex-cli` and `claude-cli`). Existing persisted legacy echo subscription profiles are repaired on startup. When launched through `mcg-orchestrator.cmd`, task execution runs from the repository root while state remains isolated from the real `.orchestrator/state.json`. Use `--no-open` for scripts or smoke tests that should host the dashboard without launching a browser.

`serve-dashboard [url] [--refresh seconds]` hosts the same dashboard from current state on a local HTTP endpoint, defaulting to `http://localhost:5087/` with a five-second browser refresh. The hosted dashboard includes setup doctor status, agent role/model and worker-profile configuration controls, direct controls for safe next actions, and browser controls for creating goals, adding custom SDLC tasks, updating verification plans, delegating work, running assigned model-backed tasks, advancing with subscription-backed dispatches, preparing worker dispatches, preparing subscription-backed worker dispatches, reporting task progress, asking goal-scoped and task-scoped human questions, starting and refreshing batch dispatches, answering pending human input, running verification commands, and recording manual pass/fail verification. When continuation stops only because background subscription work is still running, the host starts a server-side continuation watch that refreshes the process and resumes handoff after it exits; `/api/continuations` and the operator panel show watcher status. The host reloads `.orchestrator/state.json` on each request, so the page reflects changes made by other CLI commands while it is open. Press Ctrl+C in the hosting console to stop it.

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

The dashboard host also exposes local JSON endpoints for scripted monitoring and future UI surfaces:

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
- `POST /api/goals/<goal-id-prefix>/start-subscription-ready?confirmBatchStart=true`; the response includes provider-selected `Dispatches`, `ProcessPlan` ready/skipped reasons, and `Processes` for started tasks
- `POST /api/goals/<goal-id-prefix>/start-dispatches?confirmBatchStart=true`; the response includes `ProcessPlan` ready/skipped reasons and `Processes` for changed tasks
- `POST /api/goals/<goal-id-prefix>/refresh-dispatches`; the response includes `ProcessPlan` ready/skipped reasons and `Processes` for changed tasks
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/run`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/retry` with either a raw note body or JSON like `{ "message": "Retry after failed verification" }`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/dispatch` with JSON like `{ "workerName": "local", "command": "Write-Output ok" }`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/profile-dispatch` with either a raw profile name body or JSON like `{ "profileName": "codex-cli" }`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/subscription-dispatch`
- `POST /api/goals/<goal-id-prefix>/tasks/<task-number-or-id-prefix>/start`
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

## Next Slices

- Add a Windows UI or tray host once the headless behavior is stable.
