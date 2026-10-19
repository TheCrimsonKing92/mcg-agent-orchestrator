// The measured 156 s MTP chain can overlap the remaining isolated process tests.
[Xunit.CollectionDefinition(IsolatedProcessSpawningShardCollections.ShardB)]
public sealed class IsolatedProcessSpawningShardBCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;
