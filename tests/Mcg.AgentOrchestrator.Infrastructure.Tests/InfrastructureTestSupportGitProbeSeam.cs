using System.Diagnostics;
using System.Threading;

internal static partial class InfrastructureTestSupport
{
    private sealed record GitProbeSeam(
        IReadOnlyDictionary<string, string?> InheritedEnvironment,
        Func<Process, bool> StartProcess);

    private static readonly AsyncLocal<GitProbeSeam?> GitProbeSeamScope = new();

    internal static IDisposable UseGitProbeSeam(
        IReadOnlyDictionary<string, string?> inheritedEnvironment,
        Func<Process, bool> startProcess)
    {
        var previous = GitProbeSeamScope.Value;
        GitProbeSeamScope.Value = new GitProbeSeam(inheritedEnvironment, startProcess);
        return new RestoreGitProbeSeam(previous);
    }

    internal static void RequireCompleteGitOutput(WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult result)
    {
        if (result.StandardOutputTruncated)
            throw new InvalidOperationException($"git output truncated: {result}");
    }

    private sealed class RestoreGitProbeSeam(GitProbeSeam? previous) : IDisposable
    {
        public void Dispose() => GitProbeSeamScope.Value = previous;
    }
}
