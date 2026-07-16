# Acceptance Test-Impact Timing Evidence

Goal: `ceb87244b0174ad4aa536a00eb9c584c`

## 2026-06-29 - focused Infrastructure filter

Baseline before this increment:
- Command shape: `dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`
- Scope: full Infrastructure suite for narrow App/CLI changes.
- Wall clock: 9m25s / 565s.
- Source: goal objective timing record for the legacy acceptance mapping.

Focused wrapper run after this increment:
- Command: `.\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix ceb87244 -AttemptName timing-focused-cli-stable-final test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal --filter 'FullyQualifiedName~CliHelpTests'`
- Scope: actual CLI-focused Infrastructure filter emitted by RepositoryTestImpactPlanner.
- Wall clock: 23.9s.
- Result: passed, 15 tests.

Comparison:
- The emitted focused CLI run was about 18x faster than the 565s baseline.

## 2026-07-16 - seeded selected-scope failure receipt

Seeded run:
- Temporary change: inserted `Assert.True(false, "seeded selected-scope failure receipt")` inside `DashboardRenderingTests`, which is part of the focused dashboard Infrastructure gate filter.
- Command: `.\scripts\Invoke-TestSummary.ps1 -Target .\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~DashboardRenderingTests'`
- Result: failed as expected, exit code 1; TRX summary `total=72 passed=71 failed=1 skipped=0`; failing test `Dashboard_preview_resolves_test_impact_from_workspace_when_focus_changed_files_are_null`; first error line `seeded selected-scope failure receipt`.

Restored run:
- Removed the temporary failing assertion and reran the same selected-scope command.
- Result: passed, exit code 0; TRX summary `total=72 passed=72 failed=0 skipped=0`; `ALL GREEN`.
