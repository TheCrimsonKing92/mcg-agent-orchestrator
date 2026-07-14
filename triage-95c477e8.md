# Triage notes: goal 95c477e8

## Baseline

- Branch: `goal/95c477e8`
- Tip observed: `dc0bfb65`
- Current local `main`: `45cc6f53`
- Task: decide SALVAGE or ABANDON for unintegrated worker commits.

## Branch evidence

- `git rev-list --oneline main..goal/95c477e8` shows 7 branch-only commits, including tip `dc0bfb65 Add start-subscription-ready-goals blocker regression`.
- Branch-side diff from its fork point adds older ready-blocked handling:
  - `src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Workers.cs`
  - `src/Mcg.AgentOrchestrator.App/Dashboard/Api/GoalManagementCommandService.Dispatches.cs`
  - `src/Mcg.AgentOrchestrator.App/Orchestration/CrossGoalSubscriptionStartPlanner.cs`
  - new `src/Mcg.AgentOrchestrator.App/Orchestration/ReadySubscriptionBlockerDiagnostic.cs`
  - 434 lines of tests added to the old monolithic `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.cs`
- Current-main two-dot diff for focused files is stale and destructive:
  - `git diff --stat main..goal/95c477e8 -- src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Workers.cs src/Mcg.AgentOrchestrator.App/Dashboard/Api/GoalManagementCommandService.Dispatches.cs src/Mcg.AgentOrchestrator.App/Orchestration/CrossGoalSubscriptionStartPlanner.cs tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.cs tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.SubscriptionDispatchCommands.cs`
  - Result: 5 files, 6174 insertions, 4247 deletions, including deletion of current-main `CliCommandTests.SubscriptionDispatchCommands.cs` content into the old monolithic test file.

## Current main coverage

- Current main already has the readiness blocker surface using newer names and placement:
  - `CliCommandHandlers.Workers.cs` emits `READY_BLOCKED` diagnostics through `EmitReadyBlockedDiagnostics` and `EmitReadyBlockedDiagnosticsForAssigned`.
  - `GoalManagementCommandService.Dispatches.cs` has `ReadyBlockedDiagnostic` in `ParallelSafeBatchSelection` and `BuildAssignedTaskExclusionDiagnostics`.
  - Dashboard API DTO/mapper paths expose `ReadyBlockedDiagnosticDto` and ready-blocked operation results.
- Current tests already cover the core behavior the ghost branch wanted:
  - `Cli_subscription_dispatch_ready_writes_ready_blocked_lines_to_stderr_in_task_order`
  - `Cli_start_subscription_ready_writes_ready_blocked_lines_to_stderr`
  - `Dashboard_subscription_dispatch_ready_result_exposes_ready_blocked_diagnostics`
  - `Cli_start_subscription_ready_policy_gate_writes_distinct_ready_blocked_reason`
- The ghost branch's older `ReadySubscriptionBlockerDiagnostic` type is not compatible with the current-main `ReadyBlockedDiagnostic` model and would regress newer stderr-oriented diagnostics.

## Recommendation

ABANDON.

Evidence: the useful functionality already landed later on main under the `ReadyBlockedDiagnostic`/`READY_BLOCKED` implementation and tests. The branch is stale beyond safe rebase because its two-dot diff would resurrect old test/file layout and overwrite newer readiness/blocker code.

Operator command:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 abandon-goal 95c477e8 "Superseded by later ReadyBlockedDiagnostic/READY_BLOCKED implementation on main; branch is stale and would regress current ready-blocked diagnostics and test layout." --confirm-goal-abandon
```
