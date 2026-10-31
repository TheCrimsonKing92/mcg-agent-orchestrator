using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MergeTrainReproductionBuildScope : IDisposable
{
    private readonly string _root;
    internal MergeTrainReproductionBuildScope(DotnetBuildEnvironment leaseEnvironment)
    {
        _root = Path.GetFullPath(Path.Combine(leaseEnvironment.RootPath, "reproductions", Guid.NewGuid().ToString("N")));
        Environment = leaseEnvironment.DeriveArtifactsPath(Path.Combine(_root, "artifacts"));
    }
    internal DotnetBuildEnvironment Environment { get; }
    public void Dispose()
    {
        // _root is the exact, fresh GUID child created by this scope, never the lease root.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
