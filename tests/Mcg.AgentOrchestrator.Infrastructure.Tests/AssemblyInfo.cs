// Process-spawning/worker-dispatch tests mutate process-wide env vars and shared config stores.
[Xunit.CollectionDefinition("ProcessSpawning", DisableParallelization = true)]
public sealed class ProcessSpawningCollection;

// Legacy console-capture tests replace Console.Out process-wide and can break AsyncLocal captures.
[Xunit.CollectionDefinition("ConsoleOutMutation", DisableParallelization = true)]
public sealed class ConsoleOutMutationCollection;

// Provider discovery tests temporarily replace provider env vars such as OLLAMA_* and OPENAI_*.
[Xunit.CollectionDefinition("ProviderEnvironment", DisableParallelization = true)]
public sealed class ProviderEnvironmentCollection;
