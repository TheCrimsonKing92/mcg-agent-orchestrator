// Process-wide environment/current-directory mutation tests share this collection.
[Xunit.CollectionDefinition(TestCollections.EnvMutation, DisableParallelization = true)]
public sealed class EnvMutationCollection;

// Chaos gate tests run many real git operations and refresh fake dispatch records.
// Keep that fixture family serial so one adversarial gate scenario cannot perturb another.
[Xunit.CollectionDefinition(TestCollections.ChaosGateGit, DisableParallelization = true)]
public sealed class ChaosGateGitCollection;

// Dotnet build-slot tests share a run-scoped isolated root so they never touch the host slot lanes.
[Xunit.CollectionDefinition(TestCollections.DotnetBuildSlots, DisableParallelization = true)]
public sealed class DotnetBuildSlotsCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// Verifier tests can spawn nested acceptance/build activity; keep that work off the host gate slots.
[Xunit.CollectionDefinition(TestCollections.GoalAcceptanceVerifier, DisableParallelization = true)]
public sealed class GoalAcceptanceVerifierCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// Fault injection replaces LandingExecutor.GitRunner process-wide; legacy landing
// fixtures also require an isolated build root. Scoped cleanup tests need neither guard.
[Xunit.CollectionDefinition(TestCollections.LandingGitRunner, DisableParallelization = true)]
[ProcessLocalTestCollection("GitRunner and environment overrides are process-local; fixture storage roots are atomically claimed unique directories.")]
public sealed class LandingGitRunnerCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// Persistent-runner and dispatch CLI fixtures overwrite process-wide environment variables
// (worker sandbox enablement, dispatch-start suppression) that no per-operation context owns
// yet. These classes previously took that exclusion from the GoalWorktreeCleanupHooks
// definition; removing it left them naming a collection that no longer existed, so the guard
// silently became grouping without exclusion. Named explicitly here until the variables have
// owners. Every member takes a host-capacity slot as well, but that budget bounds load only.
[Xunit.CollectionDefinition(TestCollections.CliProcessEnvironment, DisableParallelization = true)]
[ProcessLocalTestCollection("Environment variables are process-local; each fixture owns its own repository root and build storage root.")]
public sealed class CliProcessEnvironmentCollection;

// Build-lease tests need a process-local isolated root but do not mutate cleanup-hook state.
[Xunit.CollectionDefinition(TestCollections.IsolatedDotnetRoot, DisableParallelization = true)]
public sealed class IsolatedDotnetRootCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// Job Object accounting tests use process-wide worker job registries, real fake worker processes,
// and slot-pinned gate processes; keep them serial and isolated from the host slot lanes.
[Xunit.CollectionDefinition(TestCollections.JobAccounting, DisableParallelization = true)]
public sealed class JobAccountingCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// Process-spawning/worker-dispatch tests mutate process-wide env vars and shared config stores;
// keep nested build activity off the host gate slots as well.
[Xunit.CollectionDefinition(TestCollections.ProcessSpawning, DisableParallelization = true)]
public sealed class ProcessSpawningCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// Process-spawning tests that only need the isolated dotnet root may overlap other collections.
[Xunit.CollectionDefinition("IsolatedProcessSpawning")]
public sealed class IsolatedProcessSpawningCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;
// Extracted modules own their remaining collection definitions.
