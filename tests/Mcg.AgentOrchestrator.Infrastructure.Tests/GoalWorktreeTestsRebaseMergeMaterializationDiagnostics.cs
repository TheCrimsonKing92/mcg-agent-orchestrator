using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeTestsRebaseMergeMaterializationDiagnostics : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void StillDirtyAfterNoOpCheckoutReportsPerCandidateEvidence()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareRawDefect(repo, goalId);
            var checkoutCalls = 0;

            GitCli.GitResult Runner(string directory, string[] args)
            {
                if (args.SequenceEqual(["status", "--porcelain=v1", "-z", "--untracked-files=all"]))
                    return new GitCli.GitResult(0, " M fixture.txt\0", string.Empty);
                if (args.SequenceEqual(["checkout", "--", "fixture.txt"]))
                {
                    checkoutCalls++;
                    return new GitCli.GitResult(0, "checkout-out-sentinel", "checkout-err-sentinel");
                }
                if (args.SequenceEqual(["ls-files", "--eol", "--", "fixture.txt"]))
                    return new GitCli.GitResult(0, "eol-sentinel", string.Empty);
                if (args.SequenceEqual(["ls-files", "--debug", "--", "fixture.txt"]))
                    return new GitCli.GitResult(0, "debug-sentinel", string.Empty);
                return GitCli.Run(directory, args);
            }

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree, GoalWorktrees.BranchName(goalId), fixture.BaseBranch, goalId, Runner);
            var bytes = File.ReadAllBytes(fixture.FixturePath);
            var length = bytes.Length.ToString(CultureInfo.InvariantCulture);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var time = File.GetLastWriteTimeUtc(fixture.FixturePath).ToString("o", CultureInfo.InvariantCulture);

            Assert.Equal(GoalWorktreeRebaseStatus.IncompleteMaterialization, result.Status);
            Assert.Equal(1, checkoutCalls);
            Assert.StartsWith("Rebase completed, but checkout materialization is incomplete", result.Message, StringComparison.Ordinal);
            Assert.Contains("worktree is still dirty after rematerialization", result.Message, StringComparison.Ordinal);
            foreach (var prefix in new[] { "before", "after" })
            {
                Assert.Contains($"{prefix}.length={length}", result.Message, StringComparison.Ordinal);
                Assert.Contains($"{prefix}.sha256={hash}", result.Message, StringComparison.Ordinal);
                Assert.Contains($"{prefix}.lastWriteUtc={time}", result.Message, StringComparison.Ordinal);
            }
            Assert.Contains("checkout.exit=0", result.Message, StringComparison.Ordinal);
            Assert.Contains("checkout-out-sentinel", result.Message, StringComparison.Ordinal);
            Assert.Contains("checkout-err-sentinel", result.Message, StringComparison.Ordinal);
            Assert.Contains("eol-sentinel", result.Message, StringComparison.Ordinal);
            Assert.Contains("debug-sentinel", result.Message, StringComparison.Ordinal);
            Assert.Contains("statusLines=[' M fixture.txt']", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void RewritingCheckoutStillReturnsRebasedWithTodaysMessage()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fixture = PrepareRawDefect(repo, goalId);
            var rewritten = false;
            var diagnosticProbeCount = 0;

            GitCli.GitResult Runner(string directory, string[] args)
            {
                if (args.SequenceEqual(["status", "--porcelain=v1", "-z", "--untracked-files=all"]))
                    return new GitCli.GitResult(0, rewritten ? string.Empty : " M fixture.txt\0", string.Empty);
                if (args.SequenceEqual(["checkout", "--", "fixture.txt"]))
                {
                    File.WriteAllBytes(fixture.FixturePath, Encoding.UTF8.GetBytes("alpha\nbeta\n"));
                    rewritten = true;
                    return new GitCli.GitResult(0, string.Empty, string.Empty);
                }
                if (args.SequenceEqual(["ls-files", "--eol", "--", "fixture.txt"]) ||
                    args.SequenceEqual(["ls-files", "--debug", "--", "fixture.txt"]))
                    diagnosticProbeCount++;
                return GitCli.Run(directory, args);
            }

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                fixture.Worktree, GoalWorktrees.BranchName(goalId), fixture.BaseBranch, goalId, Runner);

            Assert.True(rewritten);
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            Assert.Equal(0, diagnosticProbeCount);
            Assert.Equal(
                $"Rebased {GoalWorktrees.BranchName(goalId)} onto {fixture.BaseBranch}; GoalWorktrees rematerialized 1 byte-exact path(s) (fixture.txt) after proving CRLF-only conversion. Exact preimages: {result.PreimageDirectory}. Acceptance can now fast-forward after review.",
                result.Message);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static MaterializationFixture PrepareRawDefect(string repo, GoalId goalId)
    {
        RunGit(repo, "config", "core.autocrlf", "true");
        var baseBranch = RunGitOutput(repo, "branch", "--show-current");
        var worktree = GoalWorktrees.Ensure(repo, goalId);
        var fixturePath = Path.Combine(worktree, "fixture.txt");
        File.WriteAllBytes(fixturePath, Encoding.UTF8.GetBytes("alpha\nbeta\n"));
        RunGit(worktree, "add", "fixture.txt");
        RunGit(worktree, "commit", "-m", "Add byte-exact fixture");
        File.WriteAllBytes(Path.Combine(worktree, ".gitattributes"), Encoding.UTF8.GetBytes("fixture.txt -text\n"));
        RunGit(worktree, "add", ".gitattributes");
        RunGit(worktree, "commit", "-m", "Preserve fixture bytes");
        File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
        RunGit(repo, "add", "main-advanced.txt");
        RunGit(repo, "commit", "-m", "Advance main");
        RunGit(worktree, "rebase", "--merge", "--no-stat", baseBranch);
        Assert.NotEqual(
            RunGitOutput(worktree, "rev-parse", "HEAD:fixture.txt"),
            RunGitOutput(worktree, "hash-object", "--no-filters", "--", "fixture.txt"));
        return new MaterializationFixture(worktree, fixturePath, baseBranch);
    }

    private sealed record MaterializationFixture(string Worktree, string FixturePath, string BaseBranch);
}
