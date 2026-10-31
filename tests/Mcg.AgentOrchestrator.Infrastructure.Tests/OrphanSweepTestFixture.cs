using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: callers supply a private repository; every hook and clock is local.
internal sealed class OrphanSweepTestFixture(string repository)
{
    internal static readonly DateTimeOffset ReferenceTime =
        new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    internal List<OrphanWorktreeSweepOutcome> Outcomes { get; } = [];
    internal List<GoalWorktreeCleanupWarning> Warnings { get; } = [];
    internal List<string> Deletions { get; } = [];

    internal GoalWorktreeCleanupHooks Hooks => new()
    {
        BuildStorageRoot = new DotnetBuildStorageRoot(Path.Combine(repository, ".orchestrator", "test-dotnet")),
        CleanupAttentionStoreDirectory = () => Path.Combine(repository, ".orchestrator"),
        CleanupUtcNow = () => ReferenceTime,
        OrphanSweepOutcomeSink = Outcomes.Add,
        CleanupWarningSink = Warnings.Add,
        DeleteDirectoryForCleanup = path =>
        {
            Deletions.Add(path);
            Directory.Delete(path, recursive: true);
            return new GoalWorktreeDeleteResult(true, GoalWorktreeDeleteFailureKind.None, null);
        },
        ResetSandboxAcl = (_, _) => throw new InvalidOperationException("Unexpected ACL reset."),
        BuildServerShutdown = (_, _) => throw new InvalidOperationException("Unexpected build shutdown.")
    };

    internal string AddDirectory(string name)
    {
        var path = Path.Combine(repository, GoalWorktrees.DirectoryName, name);
        Directory.CreateDirectory(Path.Combine(path, "nested"));
        File.WriteAllText(Path.Combine(path, "nested", "content.txt"), "preserve until eligible");
        AgeTree(path, ReferenceTime.AddHours(-1));
        return path;
    }

    internal static void AgeTree(string path, DateTimeOffset timestamp)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, timestamp.UtcDateTime);
        foreach (var child in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            Directory.SetLastWriteTimeUtc(child, timestamp.UtcDateTime);
        Directory.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
    }

    internal OrphanWorktreeSweepOutcome AssertOutcome(string path, string reason, GoalId? goalId = null)
    {
        var outcome = Assert.Single(Outcomes, item => item.Path == path);
        Assert.Equal(reason, outcome.Reason);
        Assert.Equal(goalId, outcome.GoalId);
        Assert.Equal(Environment.ProcessId, outcome.ProcessId);
        Assert.Equal(Environment.ProcessPath, outcome.ExecutablePath);
        Assert.Equal("orphan-sweep-regression", outcome.CallerRole);
        return outcome;
    }

    internal GoalWorktreeSweepResult Sweep(AgentOrchestratorKernel? kernel = null,
        GoalWorktreeCleanupHooks? hooks = null) =>
        GoalWorktrees.SweepOrphanedWorktrees(repository, kernel, hooks ?? Hooks,
            callerRole: "orphan-sweep-regression");
}
