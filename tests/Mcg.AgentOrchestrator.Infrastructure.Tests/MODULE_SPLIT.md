# Infrastructure test module split

This plan starts from the 18 manifest lanes and uses dependency, fixture, and exclusive-resource
cohesion as the boundary evidence. Runtime balance is not a module boundary: the three remainder
lanes are dissolved below instead of becoming projects named "Remainder".

## First slice: Provider environment

`ProviderDefaultTests` and `ProviderProbeTests` form the first module. They are the only two classes
in the former Provider environment lane, all 18 discovered cases are facts, and both use the
`ProviderEnvironment` collection because they mutate `OLLAMA_*` and `OPENAI_*`. The module references
Core, App, and Infrastructure directly and owns its two small test helpers. The parent project excludes
the module directory, so each source test is compiled by exactly one project.

The gate now runs this project as a broad project check. Extracted projects currently use the verifier's
sequential check path, not the parent project's infrastructure-lane scheduler, so the check intentionally
declares neither `estimatedSerialSeconds` nor `exclusiveResourceKeys`: those fields are not consumed on
this path. `GoalAcceptanceVerifier_extracted_infrastructure_project_does_not_claim_shard_scheduler_metadata`
pins that boundary. The module-local collection remains `DisableParallelization` because its own tests
still share environment variables. Cross-project scheduling belongs to goal `780f1790`; this slice does
not claim an `xunit:EnvMutation` exclusion that the current scheduler cannot enforce.

The 3,367-test figure in the goal text is stale for this HEAD. A temporary pre-split project built over
the same source set discovered 3,375 unattended cases through the 18 original manifest filters after the
gate's HostIntegration and AcceptanceOptIn exclusions. After extraction, the 17 parent lanes discover
3,357 cases and the new broad project discovers 18, for the same 3,375 aggregate. The temporary baseline
project was removed after the comparison, so 3,375 is a historical receipt rather than a reproducible
checked-in baseline artifact; the post-split 3,357+18 side remains reproducible from the manifest and two
checked-in projects. Executed-count evidence is recorded below; discovery alone is not treated as proof
that the move preserved execution.

| Executed slice | Command shape | Total | Passed | Failed | Skipped |
| --- | --- | ---: | ---: | ---: | ---: |
| Before, current `main` parent project | `Invoke-TestSummary.ps1` with `FullyQualifiedName~ProviderDefaultTests|FullyQualifiedName~ProviderProbeTests` | 18 | 18 | 0 | 0 |
| After, extracted project | `Invoke-TestSummary.ps1 -Target ...Infrastructure.ProviderEnvironment.Tests.csproj` | 18 | 18 | 0 | 0 |

Both commands completed on 2026-08-09. Together with the 3,375 versus 3,357+18 discovery comparison,
this shows that the moved slice is neither dropped nor duplicated without requiring a second full
3,000-test execution merely to repeat tests unaffected by the extraction.

### Build-graph skipping receipt (2026-08-09)

The sanctioned worker build first built Infrastructure, the parent test project, and ProviderEnvironment
with `Invoke-WorkerBuildCheck.ps1` (exit 0, zero errors). Immediately before and after a second build of
only `Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`, the extracted assembly had these values:

| Observation | SHA-256 | LastWriteTimeUtc |
| --- | --- | --- |
| Before unrelated parent-project build | `27F8F1E61D61DEB66BBB25715831451B1E0CAB112B3AAA13C1B425CEC5E3F558` | `2026-08-09T13:40:09.4040485Z` |
| After unrelated parent-project build | `27F8F1E61D61DEB66BBB25715831451B1E0CAB112B3AAA13C1B425CEC5E3F558` | `2026-08-09T13:40:09.4040485Z` |

The exact second command was
`.\scripts\Invoke-WorkerBuildCheck.ps1 tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`;
it exited 0 with zero errors. Both the hash and timestamp remained unchanged, demonstrating that the
unrelated parent test-module build did not rewrite the extracted output. The policy regression
`GoalAcceptanceVerifier_skips_extracted_project_for_unrelated_parent_test_change` separately requires the
gate to emit a dependency-closure skip receipt without invoking the extracted executable. With the live
manifest's `enforceStructuralCoverage: true`, structural coverage later re-adds a broad execution check for
every trusted project, so that policy-level skip is not a production gate-run avoidance receipt; the
unchanged extracted output hash above is the independent build-graph-skipping evidence for this criterion.

## Second slice: fixture-free CLI parsing

The extracted CLI project owns the two classes that meet the lane boundary without importing the umbrella
project's fragile fixture: `CliArgumentNormalizationTests` (15 source-declared cases) and
`CliCommandTestsAddTaskCommands` (4 source-declared cases). The latter used no member of
`CliCommandTestBase`, so its unused inheritance was removed while its class and test-method identities stayed
unchanged. The parent project excludes the `Cli` directory, making each moved source file compile in exactly
one test assembly.

The remaining `CliCommandTests*` classes stay in the umbrella project. Attention, backlog intake, goal
revision, human-input supersede, portfolio, and refresh-dispatch tests use workspace, store, dashboard
projection, synthetic dispatch, or process-wide environment helpers. Goal lifecycle, persistent runner,
subscription dispatch, and terminal sweep tests retain the `GoalWorktreeCleanupHooks` boundary. `CliHelpTests`
and `GitCliTests` retain their real-process boundary. In particular,
`CliCommandTests.PersistentRunnerCommands.cs` was not changed.

No `Infrastructure.Testing` project is created in this slice. ProviderEnvironment owns `GetAvailablePort`
and `FakeSmokeProvider`; neither moved CLI class uses them. The two moved classes use no umbrella fixture or
collection and declare no exclusive resource key. The checked intersection is therefore empty, and creating
an empty support project would violate the shared-fixture rule.

The source-declared moved-case accounting is 19 before and 19 after (15 normalization + 4 add-task). The
existing recorded aggregate MTP discovery baseline is 3,375. A current-candidate aggregate discovery and
execution count, the extracted-output hash/timestamp rows, the policy-level dependency-closure skip receipt,
and the focused/standard gate results remain acceptance-owned measurements requested in
`docs/negative-controls/b526bf42.md`; the 19/19 source count is not substituted for those receipts.

## Proposed projects after the first slice

| Existing lane | Target project | Boundary evidence |
| --- | --- | --- |
| Cli | `Infrastructure.Cli.Tests` | Cheap command parsing and non-lifecycle CLI behavior; no fragile fixture. |
| Worker shell; Worker sandbox planner; Worker profiles; Worker dispatch fixtures | `Infrastructure.WorkerRuntime.Tests` | One dispatch/profile/preflight seam. Retain `xunit:EnvMutation`. |
| Dashboard validation | `Infrastructure.Dashboard.Tests` | Dashboard harness/API/rendering ownership; absorb dashboard classes currently selected through Process spawning or the final remainder. |
| Conduct watch sweep scoping; Dotnet build slots | `Infrastructure.Conductor.Tests` | Conductor lifecycle and build-slot coordination. Retain `xunit:DotnetBuildSlots`. |
| Process spawning | `Infrastructure.ProcessAndToolchain.Tests` | Real child-process, launcher, git CLI, and MTP fixtures. Retain `xunit:ProcessSpawning`. |
| Goal lifecycle commands; Goal worktree cleanup | `Infrastructure.GoalLifecycle.Tests` | Shared worktree lifecycle and cleanup hooks. Retain `xunit:GoalWorktreeCleanupHooks`. |
| Chaos gate | `Infrastructure.ChaosGate.Tests` | Adversarial git/dispatch fixture family and its assembly-local serial collection. Add a cross-project key only if another extracted project shares its git state. |
| Goal acceptance verifier; Goal acceptance build slots | `Infrastructure.GoalAcceptance.Tests` | Nested verifier/build activity. Retain `xunit:GoalAcceptanceVerifier` and `xunit:JobAccounting`. |
| Provider environment | `Infrastructure.ProviderEnvironment.Tests` | Extracted first slice; environment-mutation boundary described above. |
| Remainder balance A | `Infrastructure.Persistence.Tests` | `RunEventStoreTests` is persistence work; move other store/state classes from the final remainder here. |
| Remainder balance B | Split by ownership, never copied as a lane | `RunGoalService`, monitoring, and operator-channel cases join GoalLifecycle; verification/dispatch-recovery cases join WorkerRuntime; toolchain, isolated-dotnet, and alias cases join ProcessAndToolchain; state/performance joins Persistence; Discord joins Integrations; progressive-review glance joins ReviewWorkflow. |
| Remainder | Split into `Infrastructure.Planning.Tests`, `Infrastructure.Persistence.Tests`, `Infrastructure.Integrations.Tests`, `Infrastructure.ReviewWorkflow.Tests`, and `Infrastructure.TestHarness.Tests` | Assign by production namespace and fixture: goal/DAG/refinement/portfolio classes to Planning; stores and decision spine to Persistence; Discord/provider integrations to Integrations; pre-review/steward/provenance/model-outcome classes to ReviewWorkflow; source guards, baseline, test-coverage, and runner contract classes to TestHarness. Generate positive class filters during each extraction; do not preserve the negative catch-all. |

Each later extraction follows the same gate: nest the project below
`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/` and name it
`Mcg.AgentOrchestrator.Infrastructure.<Module>.Tests.csproj`, add one positive manifest check and an
explicit MTP invocation, prove the reviewer evidence alias resolves from that invocation (including through
the Conductor request contract), record an unchanged aggregate TRX count, and retain a cached-output receipt
showing an unrelated module change does not rewrite the extracted assembly. The nesting/name convention is
required by `IsExtractedInfrastructureTestProject`; a sibling project is trusted discovery input but is not
treated as an extracted Infrastructure module. Add the project to the explicit dependency graph until that
graph becomes manifest-driven.

The two first-slice helpers are intentionally module-local while only one extracted project exists. Before
the next extraction copies either helper again, create a non-test `Infrastructure.Testing` support project,
move shared fixtures there, and reference it from both test projects. Module-specific fixtures stay with
their owning test assembly.
