using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerStoreReferenceFixture : IDisposable
{
    internal const string EventGoalId = "0d8fe45d519133850d8fe45d51913385";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "store-reference-tests", Guid.NewGuid().ToString("N"));
    internal string StoreRoot => Path.Combine(Root, ".orchestrator");
    internal string WorkingDirectory => Path.Combine(Root, "repo");
    internal IClock Clock { get; } = new FixedClock();

    internal WorkerStoreReferenceFixture()
    {
        Directory.CreateDirectory(StoreRoot);
        Directory.CreateDirectory(WorkingDirectory);
    }

    internal string WriteSource(string relativePath, string content)
    {
        var path = Path.Combine(StoreRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    internal (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) CreateGoal(string objective)
    {
        var kernel = new AgentOrchestratorKernel(Clock);
        var task = new TaskSpec(TaskId.New(), "Implement scoped behavior.", AgentRole.Developer);
        return (kernel, kernel.CreateGoal(objective, [task]), task);
    }

    internal string WriteContext(string objective, bool includeStore = true)
    {
        var (_, goal, task) = CreateGoal(objective);
        return new WorkerArtifactWriter().Write(goal, task, WorkingDirectory,
            orchestratorStoreRoot: includeStore ? StoreRoot : null, clock: Clock);
    }

    internal StoreReferenceResolution Resolve(string line)
    {
        var reference = Xunit.Assert.Single(WorkerStoreReferenceResolver.Parse([line]));
        return WorkerStoreReferenceResolver.Resolve(reference, StoreRoot, Clock, out _);
    }

    internal static string Excerpt(string content)
    {
        var marker = WorkerStoreReferenceResolver.UntrustedBanner + Environment.NewLine + Environment.NewLine;
        var index = content.IndexOf(marker, StringComparison.Ordinal);
        Xunit.Assert.True(index >= 0, "Missing untrusted-data banner and excerpt separator.");
        return content[(index + marker.Length)..];
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 4, 2, 0, 0, TimeSpan.Zero);
    }
}
