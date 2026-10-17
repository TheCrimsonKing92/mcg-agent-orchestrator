using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;

// Parallel-safe: each case owns its temp tree/repository and measurement delegate.
public sealed class RatchetClassCeilingRemeasureTests
{
    [Theory]
    [InlineData(7, 5)]
    [InlineData(9, 5)]
    public void DivergentClassRow_WritesMeasuredTotalsAndOrderedComments(int first, int second)
    {
        var root = Directory.CreateTempSubdirectory("class-remeasure-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
            File.WriteAllLines(Path.Combine(root.FullName, "src/First.cs"),
                new[] { "partial class Widget {}" }.Concat(Enumerable.Repeat("// padding", first - 1)));
            File.WriteAllLines(Path.Combine(root.FullName, "src/Second.cs"),
                new[] { "partial class Widget {}" }.Concat(Enumerable.Repeat("// padding", second - 1)));
            var neighbor = FileRow("src/First.cs", 99, "unchanged file");
            const string other = "        new SourceClassCeiling(\"Other\", 40, 3),\n";
            var baseline = RegistryText(neighbor, ClassRow(10, 1, "base") + other);
            var main = RegistryText(neighbor, ClassRow(11, 1, "shared\n        // main") + other);
            var goal = RegistryText(neighbor, ClassRow(12, 2, "shared\n        // goal") + other);
            var path = Path.Combine(root.FullName, SourceSizeRatchet.SourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, goal);

            var plan = RegistryAwareConflictMerge.TryPlan(SourceSizeRatchet.SourcePath, baseline, main, goal,
                Encoding.UTF8.GetBytes(goal), out var reason);

            Assert.Equal("none", reason);
            Assert.NotNull(plan);
            Assert.True(RegistryAwareConflictMerge.WritePlans(root.FullName, [plan]));
            var expected = RegistryText(neighbor,
                ClassRow(first + second, 2, "shared\n        // main\n        // goal") + other);
            Assert.Equal(expected, File.ReadAllText(path));
            Assert.True(RegistryAwareConflictMerge.TryValidatePlans(root.FullName, [plan], out reason), reason);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableMeasurement_RefusesWithoutRewritingRegistry(bool unreadable)
    {
        using var fixture = new AdditiveConflictGitFixture(RatchetFiles());
        var path = Path.Combine(fixture.Worktree, SourceSizeRatchet.SourcePath);
        var before = File.ReadAllBytes(path);
        var calls = 0;
        var plan = RegistryAwareConflictMerge.TryPlan(SourceSizeRatchet.SourcePath,
            RatchetRegistry(10, 1, "base"), RatchetRegistry(11, 1, "goal A"), RatchetRegistry(12, 2, "goal B"),
            before, out var reason, (_, _) =>
            {
                calls++;
                if (unreadable) throw new UnauthorizedAccessException("unreadable class");
                return null;
            });

        Assert.NotNull(plan);
        Assert.False(RegistryAwareConflictMerge.WritePlans(fixture.Worktree, [plan]));
        Assert.Equal(1, calls);
        Assert.Equal(before, File.ReadAllBytes(path));
        fixture.AssertRestored();
    }

    [Fact]
    public void AbsentClass_RefusesMergeAndRestoresBranchWithRegistryReason()
    {
        using var fixture = new AdditiveConflictGitFixture(RatchetFiles(absentClass: true));
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));

        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, result.Status);
        Assert.Contains("reason=registry-same-key-conflict merge=none", Assert.Single(events));
        fixture.AssertRestored();
    }
}
