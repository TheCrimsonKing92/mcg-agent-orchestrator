// backlog ad7c8268: keep Infrastructure.Tests serial until the rotating full-suite failures are fixed.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

// Process-wide environment/current-directory mutation tests share this collection.
[Xunit.CollectionDefinition("EnvMutation", DisableParallelization = true)]
public sealed class EnvMutationCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>
{
}

// Process-spawning/worker-dispatch tests mutate process-wide env vars and shared config stores.
[Xunit.CollectionDefinition("ProcessSpawning", DisableParallelization = true)]
public sealed class ProcessSpawningCollection;

// Provider discovery tests temporarily replace provider env vars such as OLLAMA_* and OPENAI_*.
[Xunit.CollectionDefinition("ProviderEnvironment", DisableParallelization = true)]
public sealed class ProviderEnvironmentCollection;
