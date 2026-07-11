// Process-wide environment/current-directory mutation tests share this collection.
[Xunit.CollectionDefinition(TestCollections.EnvMutation, DisableParallelization = true)]
public sealed class EnvMutationCollection;

// Dotnet build-slot tests share a run-scoped isolated root so they never touch the host slot lanes.
[Xunit.CollectionDefinition(TestCollections.DotnetBuildSlots, DisableParallelization = true)]
public sealed class DotnetBuildSlotsCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// Verifier tests can spawn nested acceptance/build activity; keep that work off the host gate slots.
[Xunit.CollectionDefinition(TestCollections.GoalAcceptanceVerifier, DisableParallelization = true)]
public sealed class GoalAcceptanceVerifierCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// GoalWorktrees exposes cleanup hook seams for tests; keep those process-wide replacements serial.
[Xunit.CollectionDefinition(TestCollections.GoalWorktreeCleanupHooks, DisableParallelization = true)]
public sealed class GoalWorktreeCleanupHooksCollection;

// Job Object accounting tests use process-wide worker job registries, real fake worker processes,
// and slot-pinned gate processes; keep them serial and isolated from the host slot lanes.
[Xunit.CollectionDefinition(TestCollections.JobAccounting, DisableParallelization = true)]
public sealed class JobAccountingCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// Process-spawning/worker-dispatch tests mutate process-wide env vars and shared config stores.
[Xunit.CollectionDefinition(TestCollections.ProcessSpawning, DisableParallelization = true)]
public sealed class ProcessSpawningCollection;

// Provider discovery tests temporarily replace provider env vars such as OLLAMA_* and OPENAI_*.
[Xunit.CollectionDefinition(TestCollections.ProviderEnvironment, DisableParallelization = true)]
public sealed class ProviderEnvironmentCollection;
