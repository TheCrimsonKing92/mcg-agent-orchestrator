# Acceptance Test-Impact Timing Evidence

Goal: `ceb87244b0174ad4aa536a00eb9c584c`

## 2026-06-29 - focused Infrastructure filter

Baseline before this increment:
- Command shape: `dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`
- Scope: full Infrastructure suite for narrow App/CLI changes.
- Wall clock: 9m25s / 565s.
- Source: goal objective timing record for the legacy acceptance mapping.

Focused wrapper run after this increment:
- Command: `.\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix ceb87244 -AttemptName timing-focused-cli-planner test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal --filter 'FullyQualifiedName~CliCommandTests|FullyQualifiedName~CliHelpTests|FullyQualifiedName~FundamentalAliasTests'`
- Scope: actual CLI-focused Infrastructure filter emitted by RepositoryTestImpactPlanner; quote the filter so PowerShell does not split `|` into pipeline segments.
- Result: blocked in this worktree by `Cli_profile_dispatch_allows_complex_paid_subscription_start_under_size_threshold`, which creates a nested git worktree and fails with Windows `Filename too long` under the current checkout path.

Passing wrapper timing probe:
- Command: `.\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix ceb87244 -AttemptName probe-filter test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal --filter 'FullyQualifiedName~CliHelpTests'`
- Scope: single CLI Infrastructure test class proving `Invoke-IsolatedDotnet.ps1` threads `--filter` through to `dotnet test`.
- Wall clock: 31.3s.
- Result: passed, 15 tests.

Comparison:
- The passing single-class focused run was about 18x faster than the 565s baseline.
- The actual emitted multi-class CLI filter still needs a passing timing run from a shorter checkout path or a shard that excludes long-path-sensitive worktree tests.
