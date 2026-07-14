# Triage notes: goal 34647d0e

## Baseline

- Branch: `goal/34647d0e`
- Tip observed: `e70e54f`
- Current local `main`: `45cc6f53`
- Task: decide SALVAGE or ABANDON for unintegrated worker commits.

## Branch evidence

- Branch-only commits observed:
  - `e70e54fd Backlog slice: Replace hardcoded PowerShell host in LocalProcessVerifier`
  - `57a4aef6 Backlog slice: Replace hardcoded PowerShell host in LocalProcessVerifier`
  - `bbf69769 Orchestrator-committed worker edits for goal 34647d0e54024cf2a2dbaeae4fade2f9`
  - `eecd8d7c Run local verification dotnet commands directly`
- Branch-side fork diff targeted:
  - `src/Mcg.AgentOrchestrator.Infrastructure/Processes/LocalProcessVerifier.cs`
  - `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs`
  - `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InfrastructureTestSupport.cs`
  - `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LocalProcessVerifierTests.cs`
- Current-main two-dot focused diff is stale and destructive:
  - `git diff --stat main..goal/34647d0e -- src/Mcg.AgentOrchestrator.Infrastructure/Processes/LocalProcessVerifier.cs tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LocalProcessVerifierTests.cs src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InfrastructureTestSupport.cs`
  - Result: 4 files, 152 insertions, 1220 deletions, mostly deleting current `DotnetBuildEnvironmentManager` work.

## Current main coverage

- Current `LocalProcessVerifier` already launches verification commands directly without a PowerShell shell host:
  - `PrepareCommand` tokenizes a simple command into `FileName` and `Arguments`.
  - `RunCommandAsync` builds `ProcessStartInfo` with `FileName = fileName` and populates `ArgumentList`.
  - It still runs `dotnet build-server shutdown`, uses isolated artifacts for dotnet checks, registers process jobs, enforces timeout, and retries once after CS2012.
- Current tests already cover the goal's intended behavior:
  - `LocalProcessVerifier_launches_dotnet_directly_without_a_shell_host`
  - `LocalProcessVerifier_retries_once_on_CS2012_and_returns_passed`
  - `LocalProcessVerifier_leaves_dotnet_commands_without_goal_context_unchanged`
  - `LocalProcessVerifier_wraps_goal_dotnet_verification_with_isolated_artifacts`
- The branch's focused two-dot diff would replace current build-environment management with an older version and is not a safe source for salvage.

## Recommendation

ABANDON.

Evidence: the useful LocalProcessVerifier behavior already exists on main with broader surrounding build-slot and CS2012 handling. The branch is stale beyond rebase and would delete later `DotnetBuildEnvironmentManager` work if merged.

Operator command:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 abandon-goal 34647d0e "Superseded by later LocalProcessVerifier direct-launch and build-slot handling on main; branch is stale and would delete current DotnetBuildEnvironmentManager work." --confirm-goal-abandon
```
