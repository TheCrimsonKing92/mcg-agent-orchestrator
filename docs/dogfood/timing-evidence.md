# Acceptance Test-Impact Timing Evidence

Goal: `ceb87244b0174ad4aa536a00eb9c584c`

## 2026-06-29 - focused Infrastructure filter

Baseline before this increment:
- Command shape: `dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`
- Scope: full Infrastructure suite for narrow App/CLI changes.
- Wall clock: 9m25s / 565s.
- Source: goal objective timing record for the legacy acceptance mapping.

Focused run after this increment:
- Command: `.\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix ceb87244 -AttemptName timing-focused-cli-help test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal --filter FullyQualifiedName~CliHelpTests`
- Scope: CLI-focused Infrastructure shard representative of the new planner filter path.
- Wall clock: 25.4s.
- Result: passed.

Comparison:
- Focused run was about 22x faster than the 565s baseline.
- The planner emits the broader CLI filter as `FullyQualifiedName~CliCommandTests|FullyQualifiedName~CliHelpTests|FullyQualifiedName~FundamentalAliasTests`; unit coverage verifies that emitted acceptance command. The direct PowerShell timing shell split `|`, so this evidence uses one stable CLI shard to record real wrapper timing.
