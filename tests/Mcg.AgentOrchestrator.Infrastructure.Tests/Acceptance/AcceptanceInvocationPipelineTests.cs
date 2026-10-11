using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests;

public sealed class AcceptanceInvocationPipelineTests
{
    [Fact]
    public async Task Normal_completion_retains_explicit_invocation_attribution()
    {
        var root = CreateTempRoot();
        await using var owner = CreateOwner(root, "attempt-normal");
        var invocation = owner.CreateInvocation("normal-check");
        var verifier = CreateVerifier((_, _, _, _) =>
            Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "ok")));

        var run = await owner.RunInvocationForTestsAsync(
            verifier,
            invocation,
            CreateDotnetCommandCheck("normal-check"),
            root,
            owner.CancellationToken);

        Assert.True(run.Result.Passed);
        Assert.Equal(invocation.Ordinal, run.Result.TestResultRunOrdinal);
        owner.EnsureIdentityCurrent("candidate-tree", "main", "candidate-commit");
        await owner.DrainAsync();
    }

    [Fact]
    public async Task Timeout_is_a_typed_failure_and_the_invocation_is_observed()
    {
        var root = CreateTempRoot();
        await using var owner = CreateOwner(root, "attempt-timeout");
        var invocation = owner.CreateInvocation("timeout-check");
        var verifier = CreateVerifier((_, _, timeout, _) =>
            Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                -1,
                "timed out",
                TimedOut: true,
                Timeout: timeout,
                Elapsed: timeout)));

        var run = await owner.RunInvocationForTestsAsync(
            verifier,
            invocation,
            CreateDotnetCommandCheck("timeout-check"),
            root,
            owner.CancellationToken);

        Assert.False(run.Result.Passed);
        Assert.StartsWith("acceptance-check-timeout:", run.Result.Name, StringComparison.Ordinal);
        Assert.Equal(invocation.Ordinal, run.Result.TestResultRunOrdinal);
        await owner.DrainAsync();
    }

    [Fact]
    public async Task Stall_and_clock_timeout_share_completion_and_rerun_routing()
    {
        var root = CreateTempRoot();
        await using var owner = CreateOwner(root, "attempt-stall-shape");
        var check = CreateDotnetCommandCheck("infrastructure tests: Remainder");
        var clockResult = new GoalAcceptanceVerifier.CommandResult(
            -1, "stopped", TimedOut: true, Timeout: TimeSpan.FromMinutes(40), Elapsed: TimeSpan.FromMinutes(25));
        var stallResult = clockResult with
        {
            LaneStallSilence = TimeSpan.FromSeconds(603),
            LaneStallLimit = AcceptanceLaneStallPolicy.StallLimit
        };

        async Task<AcceptanceCheckResult> Run(GoalAcceptanceVerifier.CommandResult commandResult)
        {
            var invocation = owner.CreateInvocation(check.Name);
            var run = await owner.RunInvocationForTestsAsync(
                CreateVerifier((_, _, _, _) => Task.FromResult(commandResult)),
                invocation, check, root, owner.CancellationToken);
            Assert.Equal(invocation.Ordinal, run.Result.TestResultRunOrdinal);
            return run.Result;
        }

        var clock = await Run(clockResult);
        var stall = await Run(stallResult);
        Assert.False(stall.Passed);
        Assert.StartsWith("acceptance-check-timeout: infrastructure-tests-remainder ", stall.Name, StringComparison.Ordinal);
        Assert.Contains("reason=lane-stalled silent=603 limit=600", stall.Name, StringComparison.Ordinal);
        Assert.Contains("reason=lane-stalled", stall.ResultSummary, StringComparison.Ordinal);
        Assert.Equal("acceptance-check-timeout: infrastructure-tests-remainder elapsed=25m budget=40m", clock.Name);
        Assert.DoesNotContain("reason=", clock.ResultSummary ?? string.Empty, StringComparison.Ordinal);

        var clockDecision = AcceptanceShardCompletionAdjudicator.InferPartition(clock, false);
        var stallDecision = AcceptanceShardCompletionAdjudicator.InferPartition(stall, false);
        Assert.Equal(AcceptanceShardCompletionPredicates.TimedOut, stallDecision.FailedPredicate);
        Assert.True(stallDecision.TimedOut);
        Assert.Equal(clockDecision, stallDecision);
        Assert.True(FailedGoalTimedOutSelectionRerunRule.TryParseSelection(clock.Name, out var clockSelection));
        Assert.True(FailedGoalTimedOutSelectionRerunRule.TryParseSelection(stall.Name, out var stallSelection));
        Assert.Equal("infrastructure-tests-remainder", stallSelection);
        Assert.Equal(clockSelection, stallSelection);

        var partition = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = check.Name,
            Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~AcceptanceLaneStallPolicyTests"]
        };
        var cache = Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                new GoalId("12345678123456781234567812345678"), root, [partition], 5, true,
                _ => "candidate-tree", _ => "main", _ => "candidate-commit",
                () => "attempt-stall-shape", () => "manifest", () => false)));
        Assert.True(cache.ShouldRerunWithinAttempt(partition, stallDecision));
        Assert.Equal(cache.ShouldRerunWithinAttempt(partition, clockDecision),
            cache.ShouldRerunWithinAttempt(partition, stallDecision));
        await owner.DrainAsync();
    }

    [Fact]
    public async Task Cancellation_observes_the_started_invocation_before_teardown()
    {
        var root = CreateTempRoot();
        await using var owner = CreateOwner(root, "attempt-cancel");
        var invocation = owner.CreateInvocation("cancel-check");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifier = CreateVerifier(async (_, _, _, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new GoalAcceptanceVerifier.CommandResult(0, "unreachable");
        });

        var run = owner.RunInvocationForTestsAsync(
            verifier,
            invocation,
            CreateDotnetCommandCheck("cancel-check"),
            root,
            owner.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        owner.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await owner.DrainAsync();
    }

    [Fact]
    public async Task Changed_candidate_identity_invalidates_a_completed_invocation()
    {
        var root = CreateTempRoot();
        await using var owner = CreateOwner(root, "attempt-stale");
        var invocation = owner.CreateInvocation("stale-check");
        var verifier = CreateVerifier((_, _, _, _) =>
            Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "ok")));

        var run = await owner.RunInvocationForTestsAsync(
            verifier,
            invocation,
            CreateDotnetCommandCheck("stale-check"),
            root,
            owner.CancellationToken);

        Assert.True(run.Result.Passed);
        var exception = Assert.Throws<AcceptanceExecutionIdentityChangedException>(() =>
            owner.EnsureIdentityCurrent("changed-tree", "main", "candidate-commit"));
        Assert.Contains("result is stale", exception.Message, StringComparison.Ordinal);
        await owner.DrainAsync();
    }

    [Fact]
    public async Task Late_test_override_mutation_fails_loudly()
    {
        var root = CreateTempRoot();
        var overrides = new GoalAcceptanceVerifierTestOverrides();
        var verifier = new GoalAcceptanceVerifier(
            overrides,
            (_, _, _, _) => Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "unused")));
        overrides.HeartbeatInterval = TimeSpan.FromMilliseconds(1);
        await using var owner = CreateOwner(root, "attempt-late-override");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.RunOwnedAsync(
            root, goalId: null, changedFiles: [], stableSlotIndex: null, stableSlotLease: null, owner));

        Assert.Contains("changed after the verifier snapshot", exception.Message, StringComparison.Ordinal);
    }

    private static GoalAcceptanceVerifier CreateVerifier(
        Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner) =>
        new(runner);

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck CreateDotnetCommandCheck(string name) =>
        new()
        {
            Name = name,
            Type = "command",
            Command = "dotnet --info"
        };

    private static AcceptanceAttemptExecutionOwner CreateOwner(string root, string attemptId) =>
        new(
            new AcceptanceAttemptIdentity(
                attemptId,
                "goal-pipeline",
                root,
                "candidate-tree",
                "main",
                "candidate-commit",
                Path.Combine(root, attemptId),
                SlotIndex: null,
                Environment.ProcessId,
                LivenessCheckHint: null),
            new AcceptanceGateEngineSettings(),
            options: new AcceptanceRunExecutionOptions(DrainTimeout: TimeSpan.FromSeconds(2)));

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-acceptance-pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
