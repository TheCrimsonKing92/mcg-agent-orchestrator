# Negative control: inline landing stable-slot degradation

The control changed only the acceptance-coordination test in the goal worktree. Production still used a
zero-timeout nullable lease seam and emitted no degradation event.

- RED command: `dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --verbosity minimal -- --filter-class '*ConductorDriverTestsAcceptanceCoordination*' --no-ansi --progress off`
- RED result: 35 executed, 34 passed, 1 failed. `ConductorDriverInlineLandingRecordsDegradationWhenStableSlotUnavailable` failed with `Expected degradation event at ...\.orchestrator\logs\conduct-events.log.`
- GREEN command: same command after the production change.
- GREEN result: 37 executed, 37 passed. The new unavailable, acquired, and bounded-wait cases all executed.
- Deterministic retry command: `dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --verbosity minimal -- --filter-class '*DotnetBuildEnvironmentManagerTestsStableSlotArtifacts*' --no-ansi --progress off`
- Deterministic retry result: 11 executed, 11 passed; the injected clock advanced exactly two seconds in 100 ms steps without sleeping on wall-clock time.
