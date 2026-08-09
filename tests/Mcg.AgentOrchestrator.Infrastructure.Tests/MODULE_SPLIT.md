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

The gate now runs this project as a broad project check. Its measured manifest estimate remains 2.04
seconds. Extracted Infrastructure test projects join the same scheduled shard batch as the parent lanes,
so both the estimate and `xunit:EnvMutation` are consumed by the batch scheduler. The key prevents this
process from overlapping the Worker profiles and Worker dispatch lanes that also mutate process-wide
environment; `GoalAcceptanceVerifier_extracted_infrastructure_project_joins_scheduled_shard_batch`
pins that behavior. The module-local collection remains `DisableParallelization` because its own tests
still share environment variables.

The 3,367-test figure in the goal text is stale for this HEAD. A temporary pre-split project built over
the same source set discovers 3,375 unattended cases through the 18 original manifest filters after the
gate's HostIntegration and AcceptanceOptIn exclusions. After extraction, the 17 parent lanes discover
3,357 cases and the new broad project discovers 18, for the same 3,375 aggregate. The temporary baseline
project was removed after the comparison. The acceptance TRX receipt
must confirm those two executed-project counts before the next extraction begins.

### Build-graph skipping receipt (2026-08-09)

The sanctioned worker build first built Infrastructure, the parent test project, and ProviderEnvironment
with `Invoke-WorkerBuildCheck.ps1` (exit 0, zero errors). Immediately before and after a second build of
only `Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`, the extracted assembly had these values:

| Observation | SHA-256 | LastWriteTimeUtc |
| --- | --- | --- |
| Before unrelated parent-project build | `51CF4E8FE4E67D081B551649591C037772901E46874EF750100B78EEA494C722` | `2026-08-09T07:31:42.6168670Z` |
| After unrelated parent-project build | `51CF4E8FE4E67D081B551649591C037772901E46874EF750100B78EEA494C722` | `2026-08-09T07:31:42.6168670Z` |

The exact second command was
`.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-WorkerBuildCheck.ps1 tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`;
it exited 0 with zero errors. Both the hash and timestamp remained unchanged, demonstrating that the
unrelated parent test-module build did not rewrite the extracted output. The policy regression
`GoalAcceptanceVerifier_skips_extracted_project_for_unrelated_parent_test_change` separately requires the
gate to emit a dependency-closure skip receipt without invoking the extracted executable.

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
| Remainder balance B | Split by ownership, never copied as a lane | `RunGoalService`, monitoring, and operator-channel cases join GoalLifecycle; verification/dispatch-recovery cases join WorkerRuntime; toolchain, isolated-dotnet, alias, and egress-proxy cases join ProcessAndToolchain; state/performance joins Persistence; Discord joins Integrations; progressive-review glance joins ReviewWorkflow. |
| Remainder | Split into `Infrastructure.Planning.Tests`, `Infrastructure.Persistence.Tests`, `Infrastructure.Integrations.Tests`, `Infrastructure.ReviewWorkflow.Tests`, and `Infrastructure.TestHarness.Tests` | Assign by production namespace and fixture: goal/DAG/refinement/portfolio classes to Planning; stores and decision spine to Persistence; Discord/provider integrations to Integrations; pre-review/steward/provenance/model-outcome classes to ReviewWorkflow; source guards, baseline, test-coverage, and runner contract classes to TestHarness. Generate positive class filters during each extraction; do not preserve the negative catch-all. |

Each later extraction follows the same gate: one project, one positive manifest check, an explicit MTP
invocation, unchanged aggregate TRX count, and a cached-output receipt showing an unrelated module change
does not rewrite the extracted assembly.

The two first-slice helpers are intentionally module-local while only one extracted project exists. Before
the next extraction copies either helper again, create a non-test `Infrastructure.Testing` support project,
move shared fixtures there, and reference it from both test projects. Module-specific fixtures stay with
their owning test assembly.
