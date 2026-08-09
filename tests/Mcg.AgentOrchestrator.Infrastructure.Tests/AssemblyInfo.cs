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

// GoalWorktrees exposes cleanup hook seams and some CLI acceptance tests acquire real slot leases;
// keep those process-wide replacements serial and off the host gate slots.
[Xunit.CollectionDefinition(TestCollections.GoalWorktreeCleanupHooks, DisableParallelization = true)]
public sealed class GoalWorktreeCleanupHooksCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// Job Object accounting tests use process-wide worker job registries, real fake worker processes,
// and slot-pinned gate processes; keep them serial and isolated from the host slot lanes.
[Xunit.CollectionDefinition(TestCollections.JobAccounting, DisableParallelization = true)]
public sealed class JobAccountingCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// Process-spawning/worker-dispatch tests mutate process-wide env vars and shared config stores.
[Xunit.CollectionDefinition(TestCollections.ProcessSpawning, DisableParallelization = true)]
public sealed class ProcessSpawningCollection;
// Extracted modules own their remaining collection definitions.
