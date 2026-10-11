using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each digest owns its temporary index; all git results are supplied by fake runners.
public sealed class WorktreeTreeDigestFailureTests
{
    [Theory]
    [InlineData("rev-parse")]
    [InlineData("read-tree")]
    [InlineData("add")]
    [InlineData("write-tree")]
    public void FailedGitStepKeepsReasonAndReportsExitAndFirstErrorLine(string failedStep)
    {
        var calls = new List<string>();
        Assert.False(WorktreeTreeDigest.TryCompute("fake-worktree", out var digest, out var reason,
            out var failure, (_, _, args) =>
            {
                calls.Add(args[0]);
                return args[0] == failedStep
                    ? new(128, "", "fatal: unable to create index.lock\r\nsecond line", false)
                    : Succeeded(args);
            }));

        Assert.Equal("", digest);
        Assert.Equal("digest-unavailable", reason);
        Assert.NotNull(failure);
        Assert.Equal(failedStep, failure.Step);
        Assert.Equal(128, failure.ExitCode);
        Assert.False(failure.TimedOut);
        Assert.Null(failure.ExceptionType);
        Assert.Equal("fatal: unable to create index.lock", failure.StandardErrorFirstLine);
        Assert.Equal($"step={failedStep} exit=128 stderr=fatal: unable to create index.lock", failure.Describe());
        Assert.Equal(failedStep, calls[^1]);
        Assert.Equal(Array.IndexOf(new[] { "rev-parse", "read-tree", "add", "write-tree" }, failedStep) + 1,
            calls.Count);
    }

    [Theory]
    [InlineData("rev-parse")]
    [InlineData("read-tree")]
    [InlineData("add")]
    [InlineData("write-tree")]
    public void TimedOutFakeStepReportsTimeoutWithoutWaiting(string failedStep)
    {
        Assert.False(WorktreeTreeDigest.TryCompute("fake-worktree", out _, out var reason, out var failure,
            (_, _, args) => args[0] == failedStep ? new(-1, "", null, true) : Succeeded(args)));

        Assert.Equal("digest-unavailable", reason);
        Assert.NotNull(failure);
        Assert.True(failure.TimedOut);
        Assert.Equal($"step={failedStep} exit=-1 timed-out=true", failure.Describe());
    }

    [Fact]
    public void RunnerExceptionReportsExceptionType()
    {
        Assert.False(WorktreeTreeDigest.TryCompute("fake-worktree", out _, out var reason, out var failure,
            (_, _, _) => throw new IOException("fake git start failure")));

        Assert.Equal("digest-unavailable", reason);
        Assert.NotNull(failure);
        Assert.Equal("exception", failure.Step);
        Assert.Equal("IOException", failure.ExceptionType);
        Assert.Equal(-1, failure.ExitCode);
        Assert.False(failure.TimedOut);
        Assert.Equal("step=exception exit=-1 exception=IOException", failure.Describe());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(39)]
    [InlineData(41)]
    [InlineData(63)]
    [InlineData(65)]
    public void InvalidHashLengthReportsTreeHashStep(int length) =>
        AssertInvalidHash(new string('a', length));

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void NonHexHashReportsTreeHashStep(int length) =>
        AssertInvalidHash(new string('z', length));

    [Fact]
    public void EmptyObjectPathReportsRevParseStep()
    {
        Assert.False(WorktreeTreeDigest.TryCompute("fake-worktree", out _, out var reason, out var failure,
            (_, _, _) => new(0, " \r\n", null, false)));
        Assert.Equal("digest-unavailable", reason);
        Assert.NotNull(failure);
        Assert.Equal("step=rev-parse exit=0", failure.Describe());
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void SuccessfulRunnerProducesLowercaseDigestWithoutFailure(int hashLength)
    {
        var hash = new string('A', hashLength);
        var calls = new List<string>();
        Assert.True(WorktreeTreeDigest.TryCompute("fake-worktree", out var digest, out var reason,
            out var failure, (root, environment, args) =>
            {
                Assert.Equal("fake-worktree", root);
                calls.Add(args[0]);
                if (args[0] == "rev-parse")
                {
                    Assert.Null(environment);
                    Assert.Equal(["rev-parse", "--path-format=absolute", "--git-path", "objects"], args);
                }
                else
                {
                    Assert.NotNull(environment);
                    Assert.Equal("fake-objects", environment["GIT_ALTERNATE_OBJECT_DIRECTORIES"]);
                    Assert.Equal("0", environment["GIT_OPTIONAL_LOCKS"]);
                    Assert.True(Directory.Exists(environment["GIT_OBJECT_DIRECTORY"]));
                }
                return args[0] == "write-tree" ? new(0, $" {hash}\n", null, false) : Succeeded(args);
            }), reason);

        Assert.Equal(["rev-parse", "read-tree", "add", "write-tree"], calls);
        Assert.Equal("wbc1:git-tree:" + hash.ToLowerInvariant(), digest);
        Assert.Equal("matched", reason);
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("first line\r\nsecond line")]
    [InlineData("first line\nsecond line")]
    [InlineData("first line\rsecond line")]
    public void DescribeKeepsOnlyFirstErrorLine(string error)
    {
        var failure = new WorktreeTreeDigestFailure("add", 128, false, null, error);
        var description = failure.Describe();
        Assert.Equal("step=add exit=128 stderr=first line", description);
        Assert.DoesNotContain("\r", description);
        Assert.DoesNotContain("\n", description);
    }

    [Fact]
    public void DescribeBoundsLongErrorAndPreservesItsStart()
    {
        var failure = new WorktreeTreeDigestFailure("add", 128, false, null, "fatal: " + new string('x', 5000));
        var description = failure.Describe();
        Assert.Equal(240, description.Length);
        Assert.StartsWith("step=add exit=128 stderr=fatal: ", description);
        Assert.DoesNotContain("\r", description);
        Assert.DoesNotContain("\n", description);
    }

    private static void AssertInvalidHash(string hash)
    {
        Assert.False(WorktreeTreeDigest.TryCompute("fake-worktree", out var digest, out var reason,
            out var failure, (_, _, args) => args[0] == "write-tree" ? new(0, hash, null, false) : Succeeded(args)));
        Assert.Equal("", digest);
        Assert.Equal("digest-unavailable", reason);
        Assert.NotNull(failure);
        Assert.Equal("tree-hash", failure.Step);
        Assert.Equal("step=tree-hash exit=0", failure.Describe());
    }

    private static WorktreeTreeDigestGitStep Succeeded(string[] args) =>
        new(0, args[0] == "rev-parse" ? "fake-objects" : new string('a', 40), null, false);
}
