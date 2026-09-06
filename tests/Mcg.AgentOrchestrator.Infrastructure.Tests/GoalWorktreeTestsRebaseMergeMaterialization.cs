using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class GoalWorktreeTestsRebaseMergeMaterialization : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void RepairsSplitCommitMaterializationAfterRebase()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareFixture(repo, goalId, splitAttributeCommit: true);

            var rebase = GoalWorktrees.TryRebaseOntoMain(repo, goalId);
            var committedBlob = RunGitOutput(fixture.Worktree, "rev-parse", "HEAD:fixture.txt");
            var workingBlob = RunGitOutput(fixture.Worktree, "hash-object", "--no-filters", "--", "fixture.txt");

            Assert.Equal(string.Empty, fixture.StatusBefore);
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, rebase.Status);
            Assert.Equal(committedBlob, workingBlob);
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(fixture.FixturePath));
            Assert.Equal(string.Empty, ReadStatus(fixture.Worktree));
            Assert.Equal(["fixture.txt"], rebase.RematerializedFiles);
            Assert.True(Directory.Exists(rebase.PreimageDirectory));
            var preimage = Assert.Single(Directory.GetFiles(rebase.PreimageDirectory!));
            Assert.Contains((byte)'\r', File.ReadAllBytes(preimage));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void IgnoresWorkerResultArtifactDuringPostRebaseMaterialization()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareFixture(repo, goalId, splitAttributeCommit: true);
            File.WriteAllText(Path.Combine(fixture.Worktree, "WORKER_RESULT.md"), "worker receipt");

            var rebase = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, rebase.Status);
            Assert.Equal(["fixture.txt"], rebase.RematerializedFiles);
            Assert.Equal(
                RunGitOutput(fixture.Worktree, "rev-parse", "HEAD:fixture.txt"),
                RunGitOutput(fixture.Worktree, "hash-object", "--no-filters", "--", "fixture.txt"));
            Assert.Contains("WORKER_RESULT.md", ReadStatus(fixture.Worktree), StringComparison.Ordinal);
            Assert.False(GitCli.IsWorktreeDirty(fixture.Worktree));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void TogetherCommittedAttributesRemainCleanControl()
    {
        var splitRepo = CreateSeededRepository();
        var togetherRepo = CreateSeededRepository();
        try
        {
            var splitGoal = GoalId.New();
            var togetherGoal = GoalId.New();
            var split = PrepareFixture(splitRepo, splitGoal, splitAttributeCommit: true);
            var together = PrepareFixture(togetherRepo, togetherGoal, splitAttributeCommit: false);

            var splitResult = GoalWorktrees.TryRebaseOntoMain(splitRepo, splitGoal);
            var togetherResult = GoalWorktrees.TryRebaseOntoMain(togetherRepo, togetherGoal);

            Assert.Equal(string.Empty, split.StatusBefore);
            Assert.Equal(string.Empty, together.StatusBefore);
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, splitResult.Status);
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, togetherResult.Status);
            Assert.Empty(togetherResult.RematerializedFiles!);
            Assert.Null(togetherResult.PreimageDirectory);
            Assert.Equal(string.Empty, ReadStatus(together.Worktree));
            Assert.Equal(
                RunGitOutput(together.Worktree, "rev-parse", "HEAD:fixture.txt"),
                RunGitOutput(together.Worktree, "hash-object", "--no-filters", "--", "fixture.txt"));
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(together.FixturePath));
            Assert.Equal(
                RunGitOutput(split.Worktree, "rev-parse", "HEAD^{tree}"),
                RunGitOutput(together.Worktree, "rev-parse", "HEAD^{tree}"));
        }
        finally
        {
            DeleteDirectory(splitRepo);
            DeleteDirectory(togetherRepo);
        }
    }

    [Xunit.Fact]
    public void RestoreTheDefectControlExposesSplitCommitByteDrift()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareFixture(repo, goalId, splitAttributeCommit: true);

            RunGit(fixture.Worktree, "rebase", "--merge", "--no-stat", fixture.BaseBranch);

            Assert.Equal(string.Empty, fixture.StatusBefore);
            Assert.NotEqual(
                RunGitOutput(fixture.Worktree, "rev-parse", "HEAD:fixture.txt"),
                RunGitOutput(fixture.Worktree, "hash-object", "--no-filters", "--", "fixture.txt"));
            var statusAfter = ReadStatus(fixture.Worktree).TrimStart();
            Assert.True(
                statusAfter is "" or "M fixture.txt",
                $"Expected Git's cached status to be clean or report fixture.txt modified, but got '{statusAfter}'.");
            Assert.Contains((byte)'\r', File.ReadAllBytes(fixture.FixturePath));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void RepairsSplitCommitMaterializationWhenPorcelainStatusIsCacheBlind()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareRawDefect(repo, goalId);
            var statusReads = 0;
            GitCli.GitResult Runner(string workingDirectory, string[] arguments)
            {
                if (arguments.SequenceEqual(["status", "--porcelain=v1", "-z", "--untracked-files=all"]) &&
                    Interlocked.Increment(ref statusReads) <= 2)
                {
                    return new GitCli.GitResult(0, string.Empty, string.Empty);
                }

                return GitCli.Run(workingDirectory, arguments);
            }

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree,
                GoalWorktrees.BranchName(goalId),
                fixture.BaseBranch,
                goalId,
                Runner);

            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            Assert.Equal(3, statusReads);
            Assert.Equal(["fixture.txt"], result.RematerializedFiles);
            Assert.Equal(
                RunGitOutput(fixture.Worktree, "rev-parse", "HEAD:fixture.txt"),
                RunGitOutput(fixture.Worktree, "hash-object", "--no-filters", "--", "fixture.txt"));
            Assert.Equal(string.Empty, ReadStatus(fixture.Worktree));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void RefusesSemanticPostRebaseDirtWithoutWriting()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareRawDefect(repo, goalId);
            var semanticBytes = Encoding.UTF8.GetBytes("alpha\r\nbeta\r\nsemantic change\r\n");
            File.WriteAllBytes(fixture.FixturePath, semanticBytes);

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree,
                GoalWorktrees.BranchName(goalId),
                fixture.BaseBranch,
                goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.IncompleteMaterialization, result.Status);
            Assert.Contains("more than CRLF conversion", result.Message, StringComparison.Ordinal);
            Assert.Equal(semanticBytes, File.ReadAllBytes(fixture.FixturePath));
            Assert.Null(result.PreimageDirectory);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void PreservesBareCrBytesAndFailsClosed()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareRawDefect(repo, goalId);
            var mixedBytes = Encoding.UTF8.GetBytes("alpha\r\nbeta\r");
            File.WriteAllBytes(fixture.FixturePath, mixedBytes);

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree,
                GoalWorktrees.BranchName(goalId),
                fixture.BaseBranch,
                goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.IncompleteMaterialization, result.Status);
            Assert.Contains("bare CR", result.Message, StringComparison.Ordinal);
            Assert.Equal(mixedBytes, File.ReadAllBytes(fixture.FixturePath));
            Assert.Null(result.PreimageDirectory);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void ActiveGitOperationGuardPerformsZeroWrites()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareRawDefect(repo, goalId);
            var bytesBefore = File.ReadAllBytes(fixture.FixturePath);
            var marker = RunGitOutput(fixture.Worktree, "rev-parse", "--git-path", "CHERRY_PICK_HEAD");
            var markerPath = Path.IsPathRooted(marker) ? marker : Path.Combine(fixture.Worktree, marker);
            File.WriteAllText(markerPath, RunGitOutput(fixture.Worktree, "rev-parse", "HEAD"));

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree,
                GoalWorktrees.BranchName(goalId),
                fixture.BaseBranch,
                goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.IncompleteMaterialization, result.Status);
            Assert.Contains("CHERRY_PICK_HEAD", result.Message, StringComparison.Ordinal);
            Assert.Equal(bytesBefore, File.ReadAllBytes(fixture.FixturePath));
            Assert.Null(result.PreimageDirectory);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void PreservesExecutableIndexModeDuringRematerialization()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareFixture(repo, goalId, splitAttributeCommit: true, executable: true);
            var modeBefore = ReadIndexMode(fixture.Worktree);

            var result = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            Assert.Equal("100755", modeBefore);
            Assert.Equal(modeBefore, ReadIndexMode(fixture.Worktree));
            Assert.Equal(string.Empty, ReadStatus(fixture.Worktree));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void OutputDrainTimeoutFailsClosedWithoutWriting()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareRawDefect(repo, goalId);
            var bytesBefore = File.ReadAllBytes(fixture.FixturePath);
            GitCli.GitResult Runner(string workingDirectory, string[] arguments) =>
                arguments.SequenceEqual(["rev-parse", "HEAD"])
                    ? new GitCli.GitResult(0, string.Empty, string.Empty, DrainTimedOut: true)
                    : GitCli.Run(workingDirectory, arguments);

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree,
                GoalWorktrees.BranchName(goalId),
                fixture.BaseBranch,
                goalId,
                Runner);

            Assert.Equal(GoalWorktreeRebaseStatus.IncompleteMaterialization, result.Status);
            Assert.Contains("output drain timed out", result.Message, StringComparison.Ordinal);
            Assert.Equal(bytesBefore, File.ReadAllBytes(fixture.FixturePath));
            Assert.Null(result.PreimageDirectory);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void BytePreservingScanFailureFailsClosedWithoutWriting()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareRawDefect(repo, goalId);
            var bytesBefore = File.ReadAllBytes(fixture.FixturePath);
            GitCli.GitResult Runner(string workingDirectory, string[] arguments) =>
                arguments.SequenceEqual(["ls-files", "--eol", "-z"])
                    ? new GitCli.GitResult(19, string.Empty, "injected tracked-file scan refusal")
                    : GitCli.Run(workingDirectory, arguments);

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree,
                GoalWorktrees.BranchName(goalId),
                fixture.BaseBranch,
                goalId,
                Runner);

            Assert.Equal(GoalWorktreeRebaseStatus.IncompleteMaterialization, result.Status);
            Assert.Contains("tracked-file scan", result.Message, StringComparison.Ordinal);
            Assert.Contains("exit=19", result.Message, StringComparison.Ordinal);
            Assert.Equal(bytesBefore, File.ReadAllBytes(fixture.FixturePath));
            Assert.Null(result.PreimageDirectory);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void CheckoutFailurePreservesPreimageAndReportsAttemptedPath()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareRawDefect(repo, goalId);
            var bytesBefore = File.ReadAllBytes(fixture.FixturePath);
            GitCli.GitResult Runner(string workingDirectory, string[] arguments) =>
                arguments.Length > 0 && arguments[0] == "checkout"
                    ? new GitCli.GitResult(17, string.Empty, "injected checkout refusal")
                    : GitCli.Run(workingDirectory, arguments);

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree,
                GoalWorktrees.BranchName(goalId),
                fixture.BaseBranch,
                goalId,
                Runner);

            Assert.Equal(GoalWorktreeRebaseStatus.IncompleteMaterialization, result.Status);
            Assert.Contains("exit=17", result.Message, StringComparison.Ordinal);
            Assert.Contains("may have modified", result.Message, StringComparison.Ordinal);
            Assert.Equal(["fixture.txt"], result.RematerializedFiles);
            Assert.Equal(bytesBefore, File.ReadAllBytes(fixture.FixturePath));
            var preimage = Assert.Single(Directory.GetFiles(result.PreimageDirectory!));
            Assert.Equal(bytesBefore, File.ReadAllBytes(preimage));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void IdentityDriftFailsClosedBeforeRematerialization()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareRawDefect(repo, goalId);
            var bytesBefore = File.ReadAllBytes(fixture.FixturePath);
            var headReads = 0;
            GitCli.GitResult Runner(string workingDirectory, string[] arguments)
            {
                if (arguments.SequenceEqual(["rev-parse", "HEAD"]) && Interlocked.Increment(ref headReads) == 2)
                {
                    return new GitCli.GitResult(0, new string('0', 40), string.Empty);
                }

                return GitCli.Run(workingDirectory, arguments);
            }

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree,
                GoalWorktrees.BranchName(goalId),
                fixture.BaseBranch,
                goalId,
                Runner);

            Assert.Equal(GoalWorktreeRebaseStatus.IncompleteMaterialization, result.Status);
            Assert.Contains("identity", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(bytesBefore, File.ReadAllBytes(fixture.FixturePath));
            Assert.Null(result.PreimageDirectory);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void ContentFilterFailsClosedBeforeRematerialization()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareFixture(
                repo,
                goalId,
                splitAttributeCommit: true,
                attributeLine: "fixture.txt -text filter=unowned\n");
            RunGit(fixture.Worktree, "rebase", "--merge", "--no-stat", fixture.BaseBranch);
            var bytesBefore = File.ReadAllBytes(fixture.FixturePath);

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree,
                GoalWorktrees.BranchName(goalId),
                fixture.BaseBranch,
                goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.IncompleteMaterialization, result.Status);
            Assert.Contains("filter=unowned", result.Message, StringComparison.Ordinal);
            Assert.Equal(bytesBefore, File.ReadAllBytes(fixture.FixturePath));
            Assert.Null(result.PreimageDirectory);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void CliWorkspaceRebaseReportsRematerializationReceipt()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Byte-exact workspace rebase", repo);
            var fixture = PrepareFixture(repo, goal.Id, splitAttributeCommit: true);
            var candidateBefore = RunGitOutput(fixture.Worktree, "rev-parse", "HEAD");
            var baseBefore = RunGitOutput(repo, "rev-parse", "HEAD");
            var context = CreateAcceptanceContext(kernel, repo, goal);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["workspace", "rebase", goal.Id.Value[..8]],
                context));

            var candidateAfter = RunGitOutput(fixture.Worktree, "rev-parse", "HEAD");
            var workingBlob = RunGitOutput(fixture.Worktree, "hash-object", "--no-filters", "--", "fixture.txt");
            Assert.NotEqual(candidateBefore, candidateAfter);
            Assert.True(RunGitExitCode(fixture.Worktree, "merge-base", "--is-ancestor", baseBefore, candidateAfter) == 0);
            Assert.Equal(RunGitOutput(fixture.Worktree, "rev-parse", "HEAD:fixture.txt"), workingBlob);
            Assert.Contains("GoalWorktrees rematerialized", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Rematerialization receipt (owner: GoalWorktrees)", output, StringComparison.Ordinal);
            Assert.Contains("fixture.txt", output, StringComparison.Ordinal);
            Assert.Contains("Preimages:", output, StringComparison.Ordinal);
            Assert.Equal(string.Empty, ReadStatus(fixture.Worktree));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static MaterializationFixture PrepareRawDefect(string repo, GoalId goalId)
    {
        var fixture = PrepareFixture(repo, goalId, splitAttributeCommit: true);
        RunGit(fixture.Worktree, "rebase", "--merge", "--no-stat", fixture.BaseBranch);
        Assert.NotEqual(
            RunGitOutput(fixture.Worktree, "rev-parse", "HEAD:fixture.txt"),
            RunGitOutput(fixture.Worktree, "hash-object", "--no-filters", "--", "fixture.txt"));
        return fixture;
    }

    private static MaterializationFixture PrepareFixture(
        string repo,
        GoalId goalId,
        bool splitAttributeCommit,
        bool executable = false,
        string attributeLine = "fixture.txt -text\n")
    {
        RunGit(repo, "config", "core.autocrlf", "true");
        var baseBranch = RunGitOutput(repo, "branch", "--show-current");
        var worktree = GoalWorktrees.Ensure(repo, goalId);
        var fixturePath = Path.Combine(worktree, "fixture.txt");
        var attributesPath = Path.Combine(worktree, ".gitattributes");

        File.WriteAllBytes(fixturePath, Encoding.UTF8.GetBytes("alpha\nbeta\n"));
        if (!splitAttributeCommit)
        {
            File.WriteAllBytes(attributesPath, Encoding.UTF8.GetBytes(attributeLine));
        }

        RunGit(worktree, "add", splitAttributeCommit ? "fixture.txt" : ".");
        if (executable)
        {
            RunGit(worktree, "update-index", "--chmod=+x", "fixture.txt");
        }
        RunGit(worktree, "commit", "-m", splitAttributeCommit ? "Add byte-exact fixture" : "Add fixture with byte-preserving attribute");

        if (splitAttributeCommit)
        {
            File.WriteAllBytes(attributesPath, Encoding.UTF8.GetBytes(attributeLine));
            RunGit(worktree, "add", ".gitattributes");
            RunGit(worktree, "commit", "-m", "Preserve fixture bytes");
        }

        File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
        RunGit(repo, "add", "main-advanced.txt");
        RunGit(repo, "commit", "-m", "Advance main");

        return new MaterializationFixture(
            worktree,
            fixturePath,
            baseBranch,
            ReadStatus(worktree));
    }

    private static string ReadStatus(string worktree) =>
        RunGitOutput(worktree, "status", "--porcelain=v1", "--untracked-files=all");

    private static string ReadIndexMode(string worktree) =>
        RunGitOutput(worktree, "ls-files", "-s", "--", "fixture.txt").Split(' ', 2)[0];

    private sealed record MaterializationFixture(
        string Worktree,
        string FixturePath,
        string BaseBranch,
        string StatusBefore);
}
