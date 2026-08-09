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
seconds. The check uses `xunit:EnvMutation`, which improves the old manifest: the dedicated process is
isolated from the parent assembly, while the key prevents it from overlapping the Worker profiles and
Worker dispatch lanes that also mutate process-wide environment. The module-local collection remains
`DisableParallelization` because its own tests still share environment variables.

The 3,367-test figure in the goal text is stale for this HEAD. A temporary pre-split project built over
the same source set discovers 3,375 unattended cases through the 18 original manifest filters after the
gate's HostIntegration and AcceptanceOptIn exclusions. After extraction, the 17 parent lanes discover
3,357 cases and the new broad project discovers 18, for the same 3,375 aggregate. The temporary baseline
project was removed after the comparison. The acceptance TRX receipt
must confirm those two executed-project counts before the next extraction begins.

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
