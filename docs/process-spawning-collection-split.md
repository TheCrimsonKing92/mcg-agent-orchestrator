# Process-spawning collection split

The `ProcessSpawning` collection remains parallel-disabled for seven classes with concrete global or host hazards. Eleven classes move to `IsolatedProcessSpawning`, which shares `IsolatedDotnetRootFixture` but may overlap other collections. Classes within either collection still run sequentially; the performance gain comes from the two acceptance lanes being able to overlap.

## Moved to `IsolatedProcessSpawning`

| Class | Evidence that serialization is unnecessary |
| --- | --- |
| `AssemblyTempRedirectTests` | Uses GUID-scoped roots and events, with environment changes confined to child processes; it executes an existing apphost rather than a build. |
| `AssemblyTempRedirectChildSmokeTests` | Reads inherited environment, writes beneath a PID-scoped temporary root, and performs local cleanup. |
| `CitedPriorEvidenceResolverTests` | Uses fakes or unique temporary SQLite and git repositories; its integration case owns and deletes its repository. |
| `CliHelpTests` | Uses GUID-scoped repositories and child-only environment changes, and launches the existing app with `dotnet exec` rather than building it. |
| `ConductorDriverTests` | Uses GUID-scoped stores and injected or fake acceptance collaborators; dispatch-process references are fixture data rather than launches. |
| `ExcessWorkerRoundAnalysisScriptTests` | Uses GUID-scoped files and databases plus a hermetic child environment; child processes are PowerShell and git, not builds. |
| `GitCliTests` | Each repository is GUID-scoped and locally configured; spawned processes are repository-owned git commands. |
| `LaneTimingMeasurementScriptTests` | Uses GUID-scoped receipt repositories and synthetic timing data; spawned PowerShell and git processes do not build. |
| `SemanticAcceptanceTests` | Uses pure or fake collaborators except for one owned, bounded PowerShell grandchild; it has no shared store, environment mutation, or build. |
| `WorkerContextArtifactsCharacterizationTests` | Uses a unique synthetic repository and child-only git dates. |
| `WorkerDispatchTestsModelSelection` | Uses unique temporary roots and preparation-only APIs; process-wide environment mutation remains isolated in its sibling `WorkerDispatchTestsModelSelectionEnvMutation`. |

## Retained in serial `ProcessSpawning`

| Class | Required hazard | Evidence |
| --- | --- | --- |
| `ConductorSelfRelaunchTests` | Host build-slot contention | `RealBinaryBuildSelfCheckAndHandoff` performs a production app build. |
| `ConductorSuccessorSelfCheckTests` | Process-wide state observable by another test | Overwrites the fixed app-output git-head marker under `AppContext.BaseDirectory`. |
| `DispatchProcessHostTests` | Process-wide state observable by another test | Enumerates machine PowerShell processes, kills matches, and uses a shared fallback measurement path. |
| `GoalWorktreeIsolatedDotnetTests` | Host build-slot contention | Invokes isolated `dotnet test`, acquires build permits, and contains environment override scopes. |
| `LauncherScriptTests` | Process-wide state observable by another test | Launches background commands against the actual repository root using fixed launcher names and log registration. |
| `MtpTestRunnerScriptTests` | Host build-slot contention | Acquires a build permit and runs `dotnet test`. |
| `ProcessTreeGuiSuppressionTests` | Process-wide state observable by another test | Exercises the process-global Windows error mode through `SetErrorMode`. |

## Boundaries and follow-ups

The existing estimates are intentionally not re-baselined: whether their drift comes from host contention or stale constants remains undetermined without a controlled quiet-host comparison. Post-landing `shard-complete` receipts own proof that the two lanes overlap and that the maximum duration falls.

The `JobAccounting` collection has the same combined fixture-and-serialization shape and is a separate follow-up. Test-impact breadth, subsystem project splits, and whether movers can eventually drop the fixture entirely are also out of scope.
