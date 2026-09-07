using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class AcceptanceCohortWorkflowTestsBlockingVerifier
{
    [Fact]
    public async Task WaitContract_UsesExplicitReleaseAndHonorsCancellation()
    {
        AssertFixtureWaitHasNoDeadline();

        var expected = PassingResult();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var testCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        testCancellation.CancelAfter(TimeSpan.FromSeconds(30));
        var verifier = new AcceptanceCohortWorkflowTests.BlockingAcceptanceVerifier(started, release, expected);
        using var cancellationStarted = new ManualResetEventSlim();
        using var cancellationRelease = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var canceledVerifier = new AcceptanceCohortWorkflowTests.BlockingAcceptanceVerifier(
            cancellationStarted,
            cancellationRelease,
            expected);
        Task<AcceptanceVerificationResult>? run = null;
        Task<AcceptanceVerificationResult>? canceledRun = null;

        try
        {
            run = Task.Run(() => verifier.RunAsync(
                "controlled-worktree",
                cancellationToken: testCancellation.Token));

            Assert.True(
                started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                "Controlled verifier did not enter its release wait.");
            Assert.False(run.IsCompleted);
            release.Set();

            Assert.Same(expected, await run.WaitAsync(testCancellation.Token));

            canceledRun = Task.Run(() => canceledVerifier.RunAsync(
                "controlled-worktree",
                cancellationToken: cancellation.Token));

            Assert.True(
                cancellationStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                "Controlled verifier did not enter its cancellable release wait.");
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => canceledRun.WaitAsync(testCancellation.Token));
        }
        finally
        {
            release.Set();
            cancellationRelease.Set();
            cancellation.Cancel();
            testCancellation.Cancel();
        }
    }

    private static void AssertFixtureWaitHasNoDeadline()
    {
        var sourcePath = Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "AcceptanceCohortWorkflowTests.cs");
        var source = File.ReadAllText(sourcePath);
        var verifierStart = source.IndexOf(
            "sealed class BlockingAcceptanceVerifier",
            StringComparison.Ordinal);
        var verifierEnd = source.IndexOf(
            "sealed class SequenceAcceptanceVerifier",
            verifierStart,
            StringComparison.Ordinal);

        Assert.True(verifierStart >= 0 && verifierEnd > verifierStart, "Blocking verifier source region was not found.");
        var verifierSource = source[verifierStart..verifierEnd];
        Assert.Contains("release.Wait(cancellationToken);", verifierSource, StringComparison.Ordinal);
        Assert.DoesNotContain("release.Wait(TimeSpan", verifierSource, StringComparison.Ordinal);
    }

    private static AcceptanceVerificationResult PassingResult() => new(
        Passed: true,
        Skipped: false,
        ExitCode: 0,
        OutputTail: null,
        Checks: [new AcceptanceCheckResult("controlled", true, 0, null)],
        TestResultPaths: []);
}
