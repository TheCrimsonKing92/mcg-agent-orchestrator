# One-step receipts for all five test projects, and the defect the fifth exposed

Round-7 review found that criterion 0 requires one-step success for *every* test project while
executed receipts existed only for Core.Tests and Infrastructure.Tests. The operator lane ran the
remaining three. All five now have receipts, and the fifth exposed a real defect in the isolation
approach.

Measured at `722d0fee7ef9`, run from `.orchestrator-worktrees/8ab0a29d`, each with
`--property:McgIsolatedArtifactsPath=<unique root>`.

| Project | Result |
| --- | --- |
| `Mcg.AgentOrchestrator.Core.Tests` | total 920, succeeded 920 |
| `Mcg.AgentOrchestrator.Infrastructure.Tests` | total 8, succeeded 8 (class-filtered) |
| `Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests` | total 18, succeeded 18 |
| `Mcg.AgentOrchestrator.Infrastructure.Cli.Tests` | total 20, succeeded 20 |
| `Mcg.AgentOrchestrator.Dashboard.Tests` | total 97, **failed 9**, succeeded 88 |

Dashboard.Tests exits 2, which is real test failures — the one-step *invocation* works there. All
97 tests are discovered and executed. This is not the exit-5 plumbing failure.

## The nine failures are caused by the isolation, and are fixable

Every one is identical:

    Xunit.MicrosoftTestingPlatform.XunitException:
      System.IO.DirectoryNotFoundException : Could not locate repository root.

`SharedTestSupport.FindRepositoryRoot` walks up from three candidates looking for `.git`:

    Environment.CurrentDirectory
    AppContext.BaseDirectory
    Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable)

Relocating build output through `McgIsolatedArtifactsPath` puts `AppContext.BaseDirectory` outside
the repository, and the one-step driver does not run the apphost with the repository as its
working directory, so the first two candidates both fail. The third is unset, so the lookup
throws.

**Setting the third candidate fixes all nine.** Same command, same isolated root, with
`MCG_ORCHESTRATOR_REPOSITORY_ROOT` set to the worktree:

    total 97, failed 0, succeeded 97

## Remedy

The one-step path must set `MCG_ORCHESTRATOR_REPOSITORY_ROOT` for the child process.
`ConductorContinuitySupervisor.cs:260` and `ConductorLoopHandoff.cs:856` already do exactly this
for processes they spawn, so the convention exists; the isolated-dotnet one-step invocation is
simply missing it.

This is a general consequence of the design rather than a Dashboard quirk: any test that locates
the repository by walking up from the assembly breaks once output moves outside the tree.
Dashboard.Tests is where it happens to be load-bearing.

## Warning about the suggested Theory

The review proposes a `Theory` over the MTP project paths as the durable in-repo evidence. That is
the right remedy, but **it will go red on Dashboard.Tests unless the environment variable is set
first**. Land the environment fix before, or in the same change as, the Theory. A Theory added
alone converts a passing suite into a failing one and costs a round.

Order: set `MCG_ORCHESTRATOR_REPOSITORY_ROOT` in the one-step invocation, then widen the contract
test to a `Theory` over all five projects, then let acceptance execute it.

## Note on the acceptance manifest

The review observes that all five `config/acceptance-manifest.json` checks carry `runner=mtp` and
execute the built apphost rather than `dotnet test`, so acceptance cannot supply one-step evidence
directly. That is why the contract test shells out. It also means the Theory is the only in-gate
proof of criterion 0 — worth stating explicitly in the evidence so the distinction between "the
suite passes" and "one-step works" is not lost again.
