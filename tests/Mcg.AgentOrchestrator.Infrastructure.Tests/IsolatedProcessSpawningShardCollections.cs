internal static class IsolatedProcessSpawningShardCollections
{
    public const string ShardA = "IsolatedProcessSpawningShardA";
    public const string ShardB = "IsolatedProcessSpawningShardB";
    public const string ShardC = "IsolatedProcessSpawningShardC";
}

// The measured 181 s evidence-reuse chain can overlap the remaining isolated process tests.
[Xunit.CollectionDefinition(IsolatedProcessSpawningShardCollections.ShardA)]
public sealed class IsolatedProcessSpawningShardACollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// The measured 156 s MTP chain can overlap the remaining isolated process tests.
[Xunit.CollectionDefinition(IsolatedProcessSpawningShardCollections.ShardB)]
public sealed class IsolatedProcessSpawningShardBCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;

// The measured 154 s dispatch/script chain can overlap the remaining isolated process tests.
[Xunit.CollectionDefinition(IsolatedProcessSpawningShardCollections.ShardC)]
public sealed class IsolatedProcessSpawningShardCCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>;
