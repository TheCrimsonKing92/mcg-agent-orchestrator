// backlog ad7c8268: keep Infrastructure.Tests serial until the rotating full-suite failures are fixed.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

// Process-wide environment/current-directory mutation tests share this collection.
[Xunit.CollectionDefinition(TestCollections.EnvMutation, DisableParallelization = true)]
public sealed class EnvMutationCollection;

// Dotnet build-slot tests share a run-scoped isolated root so they never touch the host slot lanes.
[Xunit.CollectionDefinition(TestCollections.DotnetBuildSlots, DisableParallelization = true)]
public sealed class DotnetBuildSlotsCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// GoalWorktrees exposes cleanup hook seams for tests; keep those process-wide replacements serial.
[Xunit.CollectionDefinition(TestCollections.GoalWorktreeCleanupHooks, DisableParallelization = true)]
public sealed class GoalWorktreeCleanupHooksCollection;

// Process-spawning/worker-dispatch tests mutate process-wide env vars and shared config stores.
[Xunit.CollectionDefinition(TestCollections.ProcessSpawning, DisableParallelization = true)]
public sealed class ProcessSpawningCollection;

// Provider discovery tests temporarily replace provider env vars such as OLLAMA_* and OPENAI_*.
[Xunit.CollectionDefinition(TestCollections.ProviderEnvironment, DisableParallelization = true)]
public sealed class ProviderEnvironmentCollection;
