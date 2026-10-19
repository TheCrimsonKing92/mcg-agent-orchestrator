// The measured 154 s dispatch/script chain can overlap the remaining isolated process tests.
[Xunit.CollectionDefinition(IsolatedProcessSpawningShardCollections.ShardC)]
public sealed class IsolatedProcessSpawningShardCCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;
