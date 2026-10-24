using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static LandingExecutorTests;

[Xunit.Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsRootCheckout
{
    [Fact]
    public void TrackedChangeOnIntegrationBranchHoldsWithoutTouchingTheCheckout()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/landed.txt");
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, "main");
            var main = ReadGit(repo, "rev-parse", "main");
            var integration = ReadGit(repo, "rev-parse", LandingExecutor.IntegrationBranchName);
            var readme = Path.Combine(repo, "README.md");
            File.WriteAllText(readme, "user changes\r\n");
            var before = File.ReadAllBytes(readme);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = LandingExecutor.Execute(kernel, goal, workspace);

                Assert.False(result.MainAdvanced);
                var reason = Assert.IsType<LandingDecision.Escalate>(result.Decision).Reason;
                Assert.True(LandingExecutor.IsMutationHoldEscalation(reason));
                Assert.Contains("README.md", reason);
                Assert.Equal(main, ReadGit(repo, "rev-parse", "main"));
                Assert.Equal(integration, ReadGit(repo, "rev-parse", LandingExecutor.IntegrationBranchName));
                Assert.Equal(before, File.ReadAllBytes(readme));
                Assert.Empty(ReadGit(repo, "stash", "list"));
                Assert.False(GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(repo, goal.Id)));
                Assert.False(GitCli.Run(repo, "rev-parse", "--verify", $"refs/orchestrator/landing/{goal.Id.Value}").Succeeded);
                var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
                var item = Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.LandingEscalation));
                Assert.Contains("README.md", item.Message);
            }
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Fact]
    public void HeldLandingProceedsAfterTheUserRevertsTheChange()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/landed.txt");
            File.WriteAllText(Path.Combine(repo, "README.md"), "user changes");
            var held = LandingExecutor.Execute(kernel, goal, workspace);
            Assert.False(held.MainAdvanced);
            Assert.True(LandingExecutor.IsMutationHoldEscalation(Assert.IsType<LandingDecision.Escalate>(held.Decision).Reason));
            RunGit(repo, "checkout", "--", "README.md");

            var landed = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(landed.MainAdvanced, landed.Message);
            Assert.Equal(landed.MergeCommitSha, ReadGit(repo, "rev-parse", "main"));
            Assert.Equal("goal work" + Environment.NewLine, File.ReadAllText(Path.Combine(repo, "src", "landed.txt")));
            Assert.Empty(ReadGit(repo, "status", "--porcelain", "--untracked-files=no"));
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Fact]
    public void UntrackedAndIgnoredFilesDoNotBlockTheFastForward()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/landed.txt");
            var notes = Path.Combine(repo, "notes", "user-notes.md");
            var ignored = Path.Combine(repo, ".orchestrator", "user-data.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(notes)!);
            File.WriteAllText(notes, "user notes\n");
            File.WriteAllText(ignored, "ignored user data\r\n");
            var notesBefore = File.ReadAllBytes(notes);
            var ignoredBefore = File.ReadAllBytes(ignored);
            Assert.Contains("notes/", ReadGit(repo, "status", "--porcelain"));
            RunGit(repo, "check-ignore", ".orchestrator/user-data.txt");

            // Distinguish tracked edits from the untracked and ignored files kept in both attempts.
            File.WriteAllText(Path.Combine(repo, "README.md"), "tracked user changes");
            var held = LandingExecutor.Execute(kernel, goal, workspace);
            Assert.False(held.MainAdvanced);
            Assert.True(LandingExecutor.IsMutationHoldEscalation(Assert.IsType<LandingDecision.Escalate>(held.Decision).Reason));
            RunGit(repo, "checkout", "--", "README.md");

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced, result.Message);
            Assert.Equal("goal work" + Environment.NewLine, File.ReadAllText(Path.Combine(repo, "src", "landed.txt")));
            Assert.Equal(notesBefore, File.ReadAllBytes(notes));
            Assert.Equal(ignoredBefore, File.ReadAllBytes(ignored));
            Assert.Empty(ReadGit(repo, "status", "--porcelain", "--untracked-files=no"));
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherBranchCheckoutIsNeverTouched(bool detached)
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/landed.txt");
            RunGit(repo, "checkout", "-b", "feature", "main");
            var feature = ReadGit(repo, "rev-parse", "feature");
            if (detached) RunGit(repo, "checkout", "--detach", "HEAD");
            File.WriteAllText(Path.Combine(repo, "README.md"), "user changes\r\n");
            var readme = File.ReadAllBytes(Path.Combine(repo, "README.md"));
            var head = ReadGit(repo, "rev-parse", "HEAD");
            var status = RawGit(repo, "status", "--porcelain");

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced, result.Message);
            Assert.Equal(result.MergeCommitSha, ReadGit(repo, "rev-parse", "main"));
            if (detached)
            {
                Assert.False(GitCli.Run(repo, "symbolic-ref", "-q", "HEAD").Succeeded);
                Assert.Equal(head, ReadGit(repo, "rev-parse", "HEAD"));
            }
            else Assert.Equal("refs/heads/feature", ReadGit(repo, "symbolic-ref", "-q", "HEAD"));
            Assert.Equal(feature, ReadGit(repo, "rev-parse", "feature"));
            Assert.Equal(status, RawGit(repo, "status", "--porcelain"));
            Assert.Equal(readme, File.ReadAllBytes(Path.Combine(repo, "README.md")));
            Assert.False(File.Exists(Path.Combine(repo, "src", "landed.txt")));
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UntrackedFileAtAnAddedPathHolds(bool directory)
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/landed.txt");
            var main = ReadGit(repo, "rev-parse", "main");
            var collision = Path.Combine(repo, "src", "landed.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(collision)!);
            if (directory) Directory.CreateDirectory(collision);
            var userFile = directory ? Path.Combine(collision, "user.txt") : collision;
            File.WriteAllText(userFile, "user-owned content\n");
            var before = File.ReadAllBytes(userFile);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            var reason = Assert.IsType<LandingDecision.Escalate>(result.Decision).Reason;
            Assert.True(LandingExecutor.IsMutationHoldEscalation(reason));
            Assert.Contains("src/landed.txt", reason);
            Assert.Equal(main, ReadGit(repo, "rev-parse", "main"));
            Assert.Equal(before, File.ReadAllBytes(userFile));
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Theory]
    [InlineData("status", 1, false, "")]
    [InlineData("status", 0, true, "")]
    [InlineData("status", 0, false, " M incomplete")]
    [InlineData("status", 0, false, "R  renamed\0")]
    [InlineData("diff", 1, false, "")]
    [InlineData("diff", 0, true, "")]
    [InlineData("diff", 0, false, "incomplete")]
    public void InspectionFailuresHoldBeforePublication(string command, int exit, bool drainTimeout, string output)
    {
        var repo = CreateGitRepository();
        var previous = LandingExecutor.GitRunner;
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/landed.txt");
            var main = ReadGit(repo, "rev-parse", "main");
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            LandingExecutor.GitRunner = (root, args) =>
                args[0] == command && (command == "status" || args.Contains("--diff-filter=A"))
                    ? new GitCli.GitResult(exit, output, "fixture failure", drainTimeout)
                    : GitCli.Run(root, args);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            var reason = Assert.IsType<LandingDecision.Escalate>(result.Decision).Reason;
            Assert.True(LandingExecutor.IsMutationHoldEscalation(reason));
            Assert.Contains($"git {command}", reason);
            Assert.Equal(main, ReadGit(repo, "rev-parse", "main"));
            Assert.Equal(main, ReadGit(repo, "rev-parse", LandingExecutor.IntegrationBranchName));
            Assert.False(GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(repo, goal.Id)));
        }
        finally
        {
            LandingExecutor.GitRunner = previous;
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void StatusPathsAreNulDelimitedSortedEscapedAndBounded()
    {
        var calls = new List<string>();
        var paths = Enumerable.Range(0, 11).Select(index => $"path {index:00}").Reverse();
        var status = "R  z-new\0a-old\0 M line\nbreak\0" + string.Concat(paths.Select(path => $" M {path}\0"));
        GitCli.GitResult Runner(string root, string[] args)
        {
            calls.Add(args[0]);
            return new(0, args[0] switch
            {
                "symbolic-ref" => "refs/heads/main\n",
                "status" => status,
                "diff" => string.Empty,
                _ => throw new InvalidOperationException("Unexpected git command")
            }, string.Empty);
        }

        var result = RootCheckoutLandingGuard.Inspect(Runner, "fixture-root", "main", "base", "landed");
        var repeated = RootCheckoutLandingGuard.Inspect(Runner, "fixture-root", "main", "base", "other-candidate");

        Assert.Equal(RootCheckoutLandingGuard.Outcome.Hold, result.Outcome);
        Assert.Equal(result.Reason, repeated.Reason);
        Assert.Contains("a-old, line\\u000abreak, path 00, path 01", result.Reason);
        Assert.Contains("(and 4 more)", result.Reason);
        Assert.DoesNotContain("path 08", result.Reason);
        Assert.EndsWith("commit, stash or revert these changes yourself; the landing retries automatically", result.Reason);
        Assert.Equal(new[] { "symbolic-ref", "status", "diff", "symbolic-ref", "status", "diff" }, calls);
    }

    private static void RunGit(string repo, params string[] args) => _ = RawGit(repo, args);

    private static string RawGit(string repo, params string[] args)
    {
        var result = GitCli.Run(repo, args);
        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.DrainTimedOut);
        return result.Output;
    }
}
