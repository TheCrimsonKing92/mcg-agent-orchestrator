using Mcg.AgentOrchestrator.Infrastructure;

using static LandingExecutorTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class GoalOwnedLinesPatchEquivalenceTests : HostCapacityBoundTestBase
{
    [Xunit.Fact]
    public void ConflictResolutionPreservingGoalLinesIsEquivalent()
    {
        var fixture = CreateConflictFixture(changeGoalLine: false);
        try
        {
            Assert.True(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.OldHead, fixture.NewHead,
                out var evidence, out var refusal), refusal);
            Assert.Contains("goal-owned lines", evidence, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void ConflictResolutionEditingGoalLineKeepsNotCleanRefusal()
    {
        var fixture = CreateConflictFixture(changeGoalLine: true);
        try
        {
            Assert.False(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.OldHead, fixture.NewHead,
                out _, out var refusal));
            Assert.Equal($"integration-merge-not-clean; merge={fixture.NewHead}", refusal);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    internal static (string Repository, string OldHead, string NewHead) CreateConflictFixture(bool changeGoalLine)
    {
        var repo = CreateGitRepository();
        ReadGit(repo, "config", "core.autocrlf", "false");
        var path = Path.Combine(repo, "src", "Help.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "a\nb\nc\nd\n");
        ReadGit(repo, "add", "src/Help.cs");
        ReadGit(repo, "commit", "-m", "Add help fixture");

        ReadGit(repo, "checkout", "-b", "candidate-old");
        File.WriteAllText(path, "a\nb-goal\nc\nd\n");
        ReadGit(repo, "add", "src/Help.cs");
        ReadGit(repo, "commit", "-m", "Edit goal line");
        var oldHead = ReadGit(repo, "rev-parse", "HEAD");

        ReadGit(repo, "checkout", "main");
        File.WriteAllText(path, "a\nb\nc-main\nd\n");
        ReadGit(repo, "add", "src/Help.cs");
        ReadGit(repo, "commit", "-m", "Edit adjacent main line");
        ReadGit(repo, "checkout", "candidate-old");
        var merge = GitCli.Run(repo, "merge", "--no-ff", "main");
        Assert.False(merge.Succeeded, "Adjacent edits must conflict in this fixture.");
        File.WriteAllText(path, changeGoalLine
            ? "a\nb-goal-edited\nc-main\nd\n"
            : "a\nb-goal\nc-main\nd\n");
        ReadGit(repo, "add", "src/Help.cs");
        ReadGit(repo, "commit", "--no-edit");
        return (repo, oldHead, ReadGit(repo, "rev-parse", "HEAD"));
    }
}
