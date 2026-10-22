// Deferred context fixtures share the process-owned isolated root with the other Remainder chains.
[Xunit.CollectionDefinition("IsolatedProcessSpawningRemainderC")]
public sealed class IsolatedProcessSpawningRemainderCCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;
