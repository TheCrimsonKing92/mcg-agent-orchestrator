using Mcg.AgentOrchestrator.Infrastructure;

using static LandingExecutorTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class PatchEquivalenceIntegratedMainParentTests : HostCapacityBoundTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void AdditiveConflict_MainAdvanced_UsesIntegratedParent(bool goalFirstParent)
    {
        var fixture = CreateConflictFixture(goalFirstParent);
        try
        {
            Assert.NotEqual(fixture.MainParent, ReadGit(fixture.Repository, "rev-parse", "main"));
            Assert.True(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.OldHead, fixture.MergeHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                out var evidence, out var refusal), refusal);
            Assert.Equal(IntegratedParentEvidence(fixture, fixture.MainParent, fixture.MergeHead), evidence);
            Assert.Equal(string.Empty, refusal);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void EditedGoalLine_MainAdvanced_KeepsNotCleanRefusal(bool goalFirstParent)
    {
        var fixture = CreateConflictFixture(goalFirstParent, editGoalLine: true);
        try
        {
            Assert.NotEqual(fixture.MainParent, ReadGit(fixture.Repository, "rev-parse", "main"));
            Assert.False(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.OldHead, fixture.MergeHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                out var evidence, out var refusal));
            Assert.Equal($"integration-merge-not-clean; merge={fixture.MergeHead}", refusal);
            Assert.Equal(string.Empty, evidence);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void NewestMerge_NoUniqueMainParent_PreservesOldMergeRefusal()
    {
        var fixture = CreateConflictFixture(goalFirstParent: true, advanceMain: false);
        try
        {
            ReadGit(fixture.Repository, "checkout", "candidate-old");
            ReadGit(fixture.Repository, "branch", "candidate-new");
            ReadGit(fixture.Repository, "checkout", "-b", "goal-side-change");
            ReadGit(fixture.Repository, "commit", "--allow-empty", "-m", "Goal-side empty commit");
            ReadGit(fixture.Repository, "checkout", "candidate-new");
            ReadGit(fixture.Repository, "merge", "--no-ff", "goal-side-change", "-m", "Merge goal sub-branch");
            var newHead = ReadGit(fixture.Repository, "rev-parse", "HEAD");
            Assert.Equal(fixture.MainParent, ReadGit(fixture.Repository, "merge-base", "main", newHead));
            Assert.Equal(ReadGit(fixture.Repository, "rev-parse", $"{fixture.MergeHead}^{{tree}}"),
                ReadGit(fixture.Repository, "rev-parse", $"{newHead}^{{tree}}"));
            foreach (var parent in ReadGit(fixture.Repository, "rev-list", "--parents", "-n", "1", newHead)
                         .Split(' ').Skip(1))
            {
                Assert.Equal(1, GitCli.Run(fixture.Repository, "merge-base", "--is-ancestor",
                    parent, fixture.MainParent).ExitCode);
            }

            Assert.False(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.MergeHead, newHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, out var evidence, out var refusal));
            Assert.Equal($"integration-merge-not-clean; merge={fixture.MergeHead}", refusal);
            Assert.Equal(string.Empty, evidence);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void MultipleIntegrationMerges_UsesTopologicallyNewestParent()
    {
        var fixture = CreateConflictFixture(goalFirstParent: true);
        try
        {
            var newestMainParent = ReadGit(fixture.Repository, "rev-parse", "main");
            ReadGit(fixture.Repository, "checkout", "candidate-old");
            ReadGit(fixture.Repository, "merge", "--no-ff", "main", "-m", "Integrate advanced main");
            var newestMerge = ReadGit(fixture.Repository, "rev-parse", "HEAD");
            // Keep newHead distinct from the selected merge as well.
            ReadGit(fixture.Repository, "commit", "--allow-empty", "-m", "After integration");
            var newHead = ReadGit(fixture.Repository, "rev-parse", "HEAD");
            ReadGit(fixture.Repository, "checkout", "main");
            AppendCommit(fixture.Repository, "src/main-latest.txt", "Another unrelated main commit");
            Assert.NotEqual(newestMainParent, ReadGit(fixture.Repository, "rev-parse", "main"));
            Assert.Equal(newestMerge, ReadGit(fixture.Repository, "rev-list", "--merges", "--topo-order",
                $"{newestMainParent}..{newHead}").Split('\n')[0]);

            Assert.True(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.OldHead, newHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, out var evidence, out var refusal), refusal);
            Assert.Equal(IntegratedParentEvidence(fixture, newestMainParent, newestMerge, newHead), evidence);
            Assert.Equal(string.Empty, refusal);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void LinearNewRange_OldDirtyMerge_KeepsCurrentMainEvidence()
    {
        var fixture = CreateConflictFixture(goalFirstParent: true);
        try
        {
            var currentMain = ReadGit(fixture.Repository, "rev-parse", "main");
            ReadGit(fixture.Repository, "checkout", "-b", "candidate-new", "main");
            CommitHelp(fixture.Repository, ResolvedHelp(editGoalLine: false), "Rebuild goal on main");
            var newHead = ReadGit(fixture.Repository, "rev-parse", "HEAD");
            var oldBase = ReadGit(fixture.Repository, "merge-base", "main", fixture.MergeHead);
            Assert.NotEqual(oldBase, currentMain);
            Assert.Equal(string.Empty, ReadGit(fixture.Repository, "rev-list", "--merges",
                $"{currentMain}..{newHead}"));

            Assert.True(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.MergeHead, newHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, out var evidence, out var refusal), refusal);
            Assert.Equal($"goal-owned lines {oldBase}..{fixture.MergeHead} vs {currentMain}..{newHead}: zero-context added and removed lines per file are identical", evidence);
            Assert.Equal(string.Empty, refusal);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void LinearRebase_NoIntegrationMerge_KeepsRangeDiffEvidence()
    {
        var repository = CreateGitRepository();
        try
        {
            ReadGit(repository, "config", "core.autocrlf", "false");
            var oldBase = ReadGit(repository, "rev-parse", "main");
            ReadGit(repository, "checkout", "-b", "candidate-old");
            AppendCommit(repository, "src/goal.txt", "Goal lines");
            var oldHead = ReadGit(repository, "rev-parse", "HEAD");
            ReadGit(repository, "checkout", "main");
            AppendCommit(repository, "src/main.txt", "Unrelated main lines");
            var newBase = ReadGit(repository, "rev-parse", "HEAD");
            ReadGit(repository, "checkout", "-b", "candidate-new");
            ReadGit(repository, "cherry-pick", oldHead);
            var newHead = ReadGit(repository, "rev-parse", "HEAD");
            Assert.Equal(string.Empty, ReadGit(repository, "rev-list", "--merges", $"{oldBase}..{oldHead}"));
            Assert.Equal(string.Empty, ReadGit(repository, "rev-list", "--merges", $"{newBase}..{newHead}"));

            Assert.True(GoalWorktrees.TryComputePatchEquivalence(
                repository, oldHead, newHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, out var evidence, out var refusal), refusal);
            Assert.Equal($"range-diff {oldBase}..{oldHead} vs {newBase}..{newHead}: 1/1 commits have identical patch ids in order", evidence);
            Assert.Equal(string.Empty, refusal);
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    private static ConflictFixture CreateConflictFixture(
        bool goalFirstParent, bool editGoalLine = false, bool advanceMain = true)
    {
        var repository = CreateGitRepository();
        try
        {
            ReadGit(repository, "config", "core.autocrlf", "false");
            CommitHelp(repository, "a\nb\nc\nd\n", "Add help fixture");
            var oldBase = ReadGit(repository, "rev-parse", "HEAD");
            ReadGit(repository, "checkout", "-b", "candidate-old");
            CommitHelp(repository, "a\nb\ng1\ng2\nc\nd\n", "Add goal lines");
            var oldHead = ReadGit(repository, "rev-parse", "HEAD");
            ReadGit(repository, "checkout", "main");
            CommitHelp(repository, "a\nb\nm1\nm2\nc\nd\n", "Add main lines at same place");
            var mainParent = ReadGit(repository, "rev-parse", "HEAD");

            if (goalFirstParent)
                ReadGit(repository, "checkout", "candidate-old");
            else
                ReadGit(repository, "checkout", "-b", "candidate-new");
            var merge = GitCli.Run(repository, "merge", "--no-ff",
                goalFirstParent ? "main" : "candidate-old");
            Assert.Equal(1, merge.ExitCode);
            Assert.Contains("CONFLICT", merge.Output, StringComparison.Ordinal);
            CommitHelp(repository, ResolvedHelp(editGoalLine), "Resolve keeping both sides");
            var mergeHead = ReadGit(repository, "rev-parse", "HEAD");
            Assert.Equal($"{mergeHead} {(goalFirstParent ? oldHead : mainParent)} {(goalFirstParent ? mainParent : oldHead)}",
                ReadGit(repository, "rev-list", "--parents", "-n", "1", mergeHead));
            ReadGit(repository, "checkout", "main");
            if (advanceMain)
                AppendCommit(repository, "src/main-later.txt", "Unrelated main advance");
            return new ConflictFixture(repository, oldBase, oldHead, mergeHead, mainParent);
        }
        catch
        {
            TryDeleteDirectory(repository);
            throw;
        }
    }

    private static string ResolvedHelp(bool editGoalLine) => editGoalLine
        ? "a\nb\nm1\nm2\ng1-edited\ng2\nc\nd\n"
        : "a\nb\nm1\nm2\ng1\ng2\nc\nd\n";

    private static void CommitHelp(string repository, string content, string message)
    {
        var path = Path.Combine(repository, "src", "Help.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        ReadGit(repository, "add", "src/Help.cs");
        ReadGit(repository, "commit", "-m", message);
    }

    private static string IntegratedParentEvidence(
        ConflictFixture fixture, string mainParent, string merge, string? newHead = null) =>
        $"goal-owned lines {fixture.OldBase}..{fixture.OldHead} vs {mainParent}..{newHead ?? fixture.MergeHead} (merge {merge} main parent): zero-context added and removed lines per file are identical";

    private sealed record ConflictFixture(
        string Repository, string OldBase, string OldHead, string MergeHead, string MainParent);
}
