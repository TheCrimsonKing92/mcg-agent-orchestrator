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
