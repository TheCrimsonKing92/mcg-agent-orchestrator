# Negative control: scoped-only dispatch completion

- Candidate base SHA: `c6de0a1047d2f39774bc8d0e3b03c1951f19e91e` (Developer changes are intentionally uncommitted; acceptance records the orchestrator-created candidate SHA).
- RED configuration: added `RoutineCompletionContract_ExistingHostSource_ExcludesGlobalShutdown` while retaining the original `ShutdownBuildServerOnExit` dispatch member and `DispatchProcessHost` build-server shutdown path.
- Command: `dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --verbosity minimal --filter-method '*RoutineCompletionContract_ExistingHostSource_ExcludesGlobalShutdown*'`
- RED result: 1 failed / 0 passed. `Assert.DoesNotContain() Failure: Item found in collection`; found `ShutdownBuildServerOnExit`.
- GREEN configuration: removed the routine flag/path, retained legacy unknown-property tolerance, and kept completion cleanup scoped to the dispatch worktree.
- Command: `dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --verbosity minimal --filter-method '*RoutineCompletionContract*' --filter-method '*DispatchProcessHostParametersRoundTrip*' --filter-method '*CompletedNonLocalDispatch*'`
- GREEN result: 3 passed / 0 failed. The sibling-worktree signal remained unset while the owned-worktree signal was set after the exit artifact existed.
- Explicit remediation preservation: the two `DotnetBuildEnvironmentManager*ShutsDownBuildServersBeforeReleasingPermit` tests passed 2 / 2.

## Post-landing operator receipt (nonblocking)

Pending the next ordinary non-local dispatch: record worker-exit-to-native-exit-artifact timing and confirm the routine completion record contains scoped worktree cleanup but no build-server shutdown event. No pre-change measurement is required.
