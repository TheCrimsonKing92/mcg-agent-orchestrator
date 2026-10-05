using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;

// Parallel-safe: every refusal is exercised in a unique real repository and worktree.
public sealed class GoalWorktreeTestsRebaseAdditiveConflictRefusal
{
    [Fact]
    public void UntrackedWorkerResult_RefusesNonAdditiveConflictWithOriginalMessage()
    {
        using var fixture = new AdditiveConflictGitFixture([new(WidgetPath, "base\n", "main\n", "goal\n")]);
        var artifactPath = Path.Combine(fixture.Worktree, "WORKER_RESULT.md");
        File.WriteAllText(artifactPath, "worker evidence");
        Assert.Equal("?? WORKER_RESULT.md", Git(fixture.Worktree, "status", "--porcelain=v1",
            "--untracked-files=all").Trim());
        Assert.False(GitCli.IsWorktreeDirty(fixture.Worktree));
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));

        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, result.Status);
        Assert.Equal($"Rebase of {GoalWorktrees.BranchName(fixture.GoalId)} onto " +
            $"{Git(fixture.Repository, "branch", "--show-current").Trim()} found conflicts; branch was restored to its pre-rebase state.", result.Message);
        Assert.Equal(new[] { WidgetPath }, result.ConflictFiles);
        Assert.Equal("goal\n", fixture.Read());
        Assert.Equal("worker evidence", File.ReadAllText(artifactPath));
        Assert.Equal("?? WORKER_RESULT.md", Git(fixture.Worktree, "status", "--porcelain=v1",
            "--untracked-files=all").Trim());
        Assert.False(GitCli.IsWorktreeDirty(fixture.Worktree));
        Assert.Equal($"REBASE_CONFLICT_AUTOMERGE goal={fixture.GoalId.Value[..8]} result=refused " +
            $"files={WidgetPath} hunks=1 reason=non-empty-base merge=none", Assert.Single(events));
        File.Delete(artifactPath);
        fixture.AssertRestored();
    }

    [Fact]
    public void NonEmptyBase_RefusesAndRestoresOriginalGoalHead()
    {
        using var fixture = new AdditiveConflictGitFixture([new(WidgetPath, "base\n", "main\n", "goal\n")]);

        AssertRefused(fixture, "non-empty-base");

        Assert.Equal("goal\n", fixture.Read());
    }

    [Theory]
    [InlineData("tests/WidgetTests.cs")]
    [InlineData("src/appsettings.json")]
    [InlineData("config/Widget.cs")]
    [InlineData("scripts/Widget.cs")]
    [InlineData(".orchestrator/Widget.cs")]
    [InlineData("src/Widget.csproj")]
    [InlineData("src/Widget.ps1")]
    [InlineData("src/Widget.props")]
    [InlineData("src/Widget.md")]
    public void DisallowedPath_RefusesAdditiveConflict(string path)
    {
        using var fixture = new AdditiveConflictGitFixture([new(path, BaseText, MainText, GoalText)]);

        AssertRefused(fixture, "disallowed-path");

        Assert.Equal(GoalText, fixture.Read(path));
    }

    [Fact]
    public void MixedHunks_RefusesWholeIntegrationWithoutPartialResolution()
    {
        using var fixture = new AdditiveConflictGitFixture([
            new("src/A.cs", BaseText, MainText, GoalText),
            new("src/B.cs", "base\n", "main\n", "goal\n")]);

        var receipt = AssertRefused(fixture, "non-empty-base");

        Assert.Contains("files=src/A.cs,src/B.cs hunks=2", receipt);
        Assert.Equal(GoalText, fixture.Read("src/A.cs"));
        Assert.Equal("goal\n", fixture.Read("src/B.cs"));
    }

    [Fact]
    public void FrozenPath_RefusesDespiteEmptyBase()
    {
        using var fixture = new AdditiveConflictGitFixture();

        AssertRefused(fixture, "frozen-path", ["SRC\\Widget.CS"]);

        Assert.Equal(GoalText, fixture.Read());
    }

    [Fact]
    public void WholeFileAddAdd_RefusesContentThatLooksAdditive()
    {
        using var fixture = new AdditiveConflictGitFixture([new(WidgetPath, null, MainText, GoalText)]);

        AssertRefused(fixture, "not-content-conflict");
    }

    [Fact]
    public void DeletedSourceFile_RefusesModifyDeleteConflict()
    {
        using var fixture = new AdditiveConflictGitFixture();
        Git(fixture.Repository, "rm", "--", WidgetPath);
        Commit(fixture.Repository, "Main deletes the source file");

        AssertRefused(fixture, "not-content-conflict");
        Assert.Equal(GoalText, fixture.Read());
    }

    [Fact]
    public void RenamedSourceFile_RefusesConflictAtRenameDestination()
    {
        var context = string.Join('\n', Enumerable.Range(0, 20).Select(index => $"// context {index}")) + "\n";
        using var fixture = new AdditiveConflictGitFixture([
            new(WidgetPath, context + "// base\n", context + "// main\n", context + "// goal\n")]);
        Git(fixture.Repository, "mv", WidgetPath, "src/Renamed.cs");
        Commit(fixture.Repository, "Main renames the source file");

        AssertRefused(fixture, "not-content-conflict");
        Assert.Equal(context + "// goal\n", fixture.Read());
        Assert.False(File.Exists(Path.Combine(fixture.Worktree, "src/Renamed.cs")));
    }

    [Fact]
    public void ChangedFileMode_RefusesEvenWithAdditiveContent()
    {
        using var fixture = new AdditiveConflictGitFixture();
        Git(fixture.Repository, "update-index", "--chmod=+x", "--", WidgetPath);
        Git(fixture.Repository, "commit", "-m", "Main changes the source mode");

        AssertRefused(fixture, "not-content-conflict");
        Assert.Equal(GoalText, fixture.Read());
    }

    [Fact]
    public void BinaryAttribute_RefusesSourceFileConflict()
    {
        using var fixture = new AdditiveConflictGitFixture(attributes: "*.cs binary\n");

        AssertRefused(fixture, "not-content-conflict");
    }

    [Fact]
    public void MarkerLikeSourceLine_RefusesAmbiguousResolution()
    {
        using var fixture = new AdditiveConflictGitFixture([
            new(WidgetPath, BaseText, MainText.Replace("    void Main() {}", "<<<<<<< fake"), GoalText)]);

        AssertRefused(fixture, "unparseable-markers");
    }

    private static string AssertRefused(AdditiveConflictGitFixture fixture, string reason, string[]? frozen = null)
    {
        var events = new List<string>();
        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new(frozen ?? [], events.Add));

        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, result.Status);
        Assert.Equal($"Rebase of {GoalWorktrees.BranchName(fixture.GoalId)} onto " +
            $"{Git(fixture.Repository, "branch", "--show-current").Trim()} found conflicts; branch was restored to its pre-rebase state.", result.Message);
        Assert.NotEmpty(result.ConflictFiles);
        fixture.AssertRestored();
        var receipt = Assert.Single(events);
        Assert.StartsWith($"REBASE_CONFLICT_AUTOMERGE goal={fixture.GoalId.Value[..8]} result=refused ", receipt);
        Assert.Contains($" reason={reason} merge=none", receipt);
        return receipt;
    }
}
