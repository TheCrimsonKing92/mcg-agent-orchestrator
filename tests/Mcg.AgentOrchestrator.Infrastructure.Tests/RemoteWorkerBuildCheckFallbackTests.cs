using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static RemoteWorkerBuildCheckScenarioTests;

// Parallel-safe: unique slot stores and repositories; lease/deadline decisions use signalled timers.
public sealed class RemoteWorkerBuildCheckFallbackTests
{
    [Theory]
    [InlineData("occupied", "no-idle-executor")]
    [InlineData("executor-busy", "executor-busy")]
    [InlineData("executor-tools-mismatch", "executor-tools-mismatch")]
    [InlineData("push-failed", "push-failed")]
    [InlineData("lease", "lease-expired")]
    [InlineData("deadline", "remote-budget-exhausted")]
    [InlineData("tree", "binding-mismatch-tree")]
    [InlineData("red", "remote-build-not-success")]
    [InlineData("incomplete", "remote-receipt-incomplete")]
    [InlineData("digest", "remote-receipt-digest-mismatch")]
    [InlineData("budget", "remote-budget-unavailable")]
    [InlineData("missing", "remote-receipt-missing")]
    [InlineData("schema", "remote-receipt-invalid")]
    [InlineData("project-red", "remote-receipt-incomplete")]
    [InlineData("throws", "transport-fault")]
    [InlineData("poll-throws", "transport-fault")]
    [InlineData("git-fails", "candidate-commit-unavailable")]
    [InlineData("candidate-tree", "candidate-tree-mismatch")]
    public async Task Run_RemoteFailure_ReturnsIdenticalLocalVerdict(string fault, string reason)
    {
        var fixture = new Fixture();
        if (fault == "red") fixture.LocalOutput = new(1, "Feature.cs: error CS1000: local compiler failure", "local stderr");
        var operation = fault == "budget" ? fixture.Operation with
        { Deadline = fixture.Time.GetUtcNow() + TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(59) } : fixture.Operation;
        var baseline = fixture.Run(new(), operation);
        var receiptBytes = File.ReadAllBytes(fixture.ReceiptPath);
        Assert.False(File.Exists(fixture.LedgerPath));
        fixture.LocalCalls = 0;
        fixture.Publish(fault);
        using var occupant = fixture.Coordinator("occupied-by-another-attempt");
        if (fault == "occupied") Assert.NotNull(occupant.TryClaimIdleExecutor());
        if (fault is "executor-busy" or "executor-tools-mismatch" or "push-failed")
            fixture.Executor.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, fault));
        if (fault == "throws") fixture.Executor.Submit = (_, _) => throw new IOException("fixture transport fault");
        if (fault == "poll-throws") fixture.Handle.OnPolled = () => throw new IOException("fixture poll fault");
        if (fault == "git-fails") fixture.GitRunner = (_, _, _) => new(1, "", "fixture git fault", false);
        if (fault == "candidate-tree")
        {
            fixture.GitRunner = (root, environment, args) => args[0] == "write-tree"
                ? new(0, new string('a', 40), "", false) : args[0] == "commit-tree"
                    ? new(0, new string('b', 40), "", false) : RemoteBuildCandidateCommit.RunGit(root, environment, args);
        }
        if (fault is "lease" or "deadline")
        {
            fixture.Configuration = fixture.Configuration with { Executors = [new("mcg-exec-1", fault == "lease" ? 60 : 3600)] };
            fixture.Executor.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(fixture.Handle));
        }
        var work = Task.Run(() => fixture.Run(new("remote-first", 300), operation));
        if (fault is "lease" or "deadline")
        {
            await Event(fixture.Handle.Polled.Task, "remote handle polled");
            await Event(fixture.Time.PollTimerCreated.Task, "remote poll delay registered");
            fixture.Time.Advance(TimeSpan.FromSeconds(fault == "lease" ? 61 : 300));
        }
        var actual = await Event(work, "placement returned after remote failure");
        Assert.Equal(baseline.Phase, actual.Phase);
        Assert.Equal(baseline.BuildExitCode, actual.BuildExitCode);
        Assert.Equal(baseline.StandardOutput, actual.StandardOutput);
        Assert.Equal(baseline.StandardError, actual.StandardError);
        Assert.Equal(receiptBytes, File.ReadAllBytes(fixture.ReceiptPath));
        Assert.Equal("build_evidence_host=local; remote_fallback=" + reason + "; " + baseline.Diagnostic, actual.Diagnostic);
        Assert.Equal(1, fixture.LocalCalls);
        if (fault == "red") Assert.Equal(CompletionBuildEvidencePhase.CompilerFailure, actual.Phase);
        if (fault is "budget" or "git-fails" or "candidate-tree" or "occupied") Assert.Empty(fixture.Executor.Requests);
        else Assert.Single(fixture.Executor.Requests);
        using var ledger = JsonDocument.Parse(Assert.Single(File.ReadAllLines(fixture.LedgerPath)));
        Assert.Equal("local", ledger.RootElement.GetProperty("placement").GetString());
        Assert.Equal(reason, ledger.RootElement.GetProperty("fallback_reason").GetString());
        Assert.False(File.Exists(RemoteExecutorHealthLedger.ResolveStorePath(operation.WorktreeRoot)));
        if (fault is "lease" or "deadline" or "tree" or "red" or "incomplete" or "digest") Assert.True(fixture.Handle.IsAbandoned);
    }

    [Theory]
    [InlineData("missing", "success")]
    [InlineData("missing", "no-receipt")]
    [InlineData("missing", "compiler")]
    [InlineData("off", "success")]
    [InlineData("off", "no-receipt")]
    [InlineData("off", "compiler")]
    [InlineData("fast", "success")]
    [InlineData("fast", "no-receipt")]
    [InlineData("fast", "compiler")]
    public void Run_OffOrInvalidMode_IsIdenticalToLocal(string mode, string row)
    {
        var fixture = new Fixture();
        var config = Path.Combine(fixture.Host.Root, "config.json");
        if (mode != "missing") File.WriteAllText(config,
            "{\"executors\":[],\"lanes\":[],\"workerBuildCheck\":{\"mode\":\"" + mode + "\"}}");
        var settings = RemoteLaneExecutorConfiguration.LoadForWorkerBuildCheck(config).WorkerBuildCheck;
        fixture.LocalReceipt = row == "success";
        if (row == "compiler") fixture.LocalOutput = new(1, "Feature.cs: error CS1000: broken", "compiler stderr");
        var expected = fixture.Local(fixture.Operation.Deadline - fixture.Time.GetUtcNow());
        var before = File.Exists(fixture.ReceiptPath) ? File.ReadAllBytes(fixture.ReceiptPath) : null;
        var remoteCalls = 0;
        var actual = CompletionBuildPlacement.Run(fixture.Operation, fixture.ReceiptPath, fixture.LedgerPath, settings,
            request =>
            {
                remoteCalls++;
                return RemoteWorkerBuildCheck.RunAsync(request, new(fixture.Configuration, fixture.Executor, fixture.Time,
                    TimeSpan.FromSeconds(1), fixture.GitRunner));
            }, fixture.Local, fixture.Time);
        Assert.Equal(expected, actual);
        Assert.Equal(before, File.Exists(fixture.ReceiptPath) ? File.ReadAllBytes(fixture.ReceiptPath) : null);
        Assert.Equal(0, remoteCalls); // The only path to a claim was never entered.
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.LedgerPath));
        Assert.Equal(row switch
        {
            "success" => CompletionBuildEvidencePhase.Success,
            "no-receipt" => CompletionBuildEvidencePhase.ApparatusFailure,
            _ => CompletionBuildEvidencePhase.CompilerFailure
        }, actual.Phase);
        if (row == "no-receipt") Assert.StartsWith("Build helper did not produce exact required receipt coverage.", actual.Diagnostic);
    }

    [Fact]
    public async Task Run_SubmissionDeadline_CancelsBeforeLocalBudget()
    {
        var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Submit = async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, fixture.Time, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException("Unexpected submit completion");
        };
        var work = Task.Run(() => fixture.Run(new("remote-first", 300)));
        await Event(entered.Task, "submission entered with deadline token");
        fixture.Time.Advance(TimeSpan.FromSeconds(300));
        var result = await Event(work, "cancelled submission returned");
        await Event(cancelled.Task, "submission observed cancellation");
        Assert.StartsWith("build_evidence_host=local; remote_fallback=remote-budget-exhausted; ", result.Diagnostic);
        Assert.Equal(1, fixture.LocalCalls);
    }

    [Fact]
    public async Task Run_LateSubmission_AbandonsHandleAfterLocalFallback()
    {
        var fixture = new Fixture();
        var submitted = new TaskCompletionSource<RemoteLaneSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Submit = (_, _) => submitted.Task;
        var work = Task.Run(() => fixture.Run(new("remote-first", 300)));
        await Event(fixture.Executor.Submitted.Task, "non-cooperative submission started");
        fixture.Time.Advance(TimeSpan.FromSeconds(300));
        var result = await Event(work, "local fallback returned before late submission");
        Assert.StartsWith("build_evidence_host=local; remote_fallback=remote-budget-exhausted; ", result.Diagnostic);
        Assert.Equal(1, fixture.LocalCalls);
        submitted.SetResult(new(fixture.Handle));
        await Event(fixture.Handle.Abandoned.Task, "late handle abandoned");
    }

    [Fact]
    public void Run_HostChangesDuringRemoteBuild_DiscardsTranslatedReceipt()
    {
        var fixture = new Fixture();
        fixture.Publish();
        var submit = fixture.Executor.Submit;
        fixture.Executor.Submit = async (request, token) =>
        {
            var result = await submit(request, token);
            File.AppendAllText(Path.Combine(fixture.Operation.WorktreeRoot, "src/Feature/Feature.cs"), "\n// host changed");
            return result;
        };
        var result = fixture.Run(new("remote-first", 300));
        Assert.StartsWith("build_evidence_host=local; remote_fallback=host-receipt-unverified; ", result.Diagnostic);
        Assert.Equal(CompletionBuildEvidencePhase.Success, result.Phase);
        Assert.Equal(1, fixture.LocalCalls);
        using var receipt = JsonDocument.Parse(File.ReadAllText(fixture.ReceiptPath));
        Assert.False(receipt.RootElement.TryGetProperty("executorId", out _));
        Assert.True(WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Operation.WorktreeRoot).Matches);
    }

    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
    private static async Task<T> Event<T>(Task<T> task, string name)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
}
