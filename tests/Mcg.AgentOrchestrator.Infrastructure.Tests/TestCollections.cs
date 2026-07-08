using Mcg.AgentOrchestrator.Infrastructure;

public static class TestCollections
{
    public const string DotnetBuildSlots = "DotnetBuildSlots";
    public const string EnvMutation = "EnvMutation";
    public const string GoalWorktreeCleanupHooks = "GoalWorktreeCleanupHooks";
    public const string ProcessSpawning = "ProcessSpawning";
    public const string ProviderEnvironment = "ProviderEnvironment";
}

// Pins MCG_DOTNET_ISOLATED_ROOT to an ephemeral per-run temp root for the dotnet
// build-slot collection. Per-test EnvVarScope overrides nest cleanly on top of
// this baseline and the collection root is deleted only after the slot tests finish.
public sealed class IsolatedDotnetRootFixture : IDisposable
{
    private readonly string? _originalValue;
    private readonly string _root;

    public IsolatedDotnetRootFixture()
    {
        _originalValue = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        _root = Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-slot-run-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _originalValue);
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
