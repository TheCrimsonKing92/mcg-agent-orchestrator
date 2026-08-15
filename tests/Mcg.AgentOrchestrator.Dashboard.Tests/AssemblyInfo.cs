// Collection definitions are assembly-local; keep the moved tests serialized exactly as before.
[Xunit.CollectionDefinition(TestCollections.EnvMutation, DisableParallelization = true)]
public sealed class EnvMutationCollection;

[Xunit.CollectionDefinition(TestCollections.ProcessSpawning, DisableParallelization = true)]
public sealed class ProcessSpawningCollection;
