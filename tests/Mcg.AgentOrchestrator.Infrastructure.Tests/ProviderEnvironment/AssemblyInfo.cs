// Provider discovery tests temporarily replace provider env vars such as OLLAMA_* and OPENAI_*.
// This definition stays non-parallel even in the dedicated assembly: tests within the collection
// mutate the same process-wide environment, so the assembly boundary only isolates other modules.
[Xunit.CollectionDefinition("ProviderEnvironment", DisableParallelization = true)]
public sealed class ProviderEnvironmentCollection;
