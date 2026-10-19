// The measured 181 s evidence-reuse chain can overlap the remaining isolated process tests.
[Xunit.CollectionDefinition(IsolatedProcessSpawningShardCollections.ShardA)]
public sealed class IsolatedProcessSpawningShardACollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;
