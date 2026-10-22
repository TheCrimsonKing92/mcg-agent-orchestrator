using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories, slot store, artifacts and injected clock/transport.
public sealed class RemoteWorkerBuildCheckScenarioTests
{
    [Fact]
    public void Run_DirtyCandidate_BindsRemoteBuildWithoutLocalExecution()
    {
        var fixture = new Fixture();
        var before = Snapshot(fixture.Operation.WorktreeRoot);
        fixture.Publish();
        var result = fixture.Run(new("remote-first", 300));
        Assert.Equal(CompletionBuildEvidencePhase.Success, result.Phase);
        Assert.Equal(0, result.BuildExitCode);
        Assert.Equal(0, fixture.LocalCalls);
        Assert.StartsWith("build_evidence_host=remote:mcg-exec-1; candidate_commit=", result.Diagnostic);
        var request = Assert.Single(fixture.Executor.Requests);
        Assert.Equal("build-check", request.Kind);
        Assert.Equal(fixture.Operation.Projects, request.BuildProjects);
        Assert.Equal("worker-build-check", request.Lane);
        Assert.Equal("", request.Filter);
        Assert.Equal("worker-build-check:v1", request.ManifestIdentity);
        Assert.Equal(fixture.Operation.TreeDigest, "wbc1:git-tree:" + Git(fixture.Operation.WorktreeRoot, "rev-parse", request.VerifyingCommitSha + "^{tree}"));
        var receipt = WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Operation.WorktreeRoot);
        Assert.Equal("matched", receipt.Reason);
        Assert.Equal(fixture.Operation.Projects, receipt.Projects);
        using var host = JsonDocument.Parse(File.ReadAllText(fixture.ReceiptPath));
        Assert.Equal("mcg-exec-1", host.RootElement.GetProperty("executorId").GetString());
        Assert.Equal(request.VerifyingCommitSha, host.RootElement.GetProperty("candidateCommit").GetString());
        Assert.Equal(fixture.Operation.TreeDigest, host.RootElement.GetProperty("finalTreeDigest").GetString());
        Assert.Equal(Fixture.Pass, File.ReadAllText(fixture.Operation.ResultPath + ".stdout"));
        Assert.Equal("", File.ReadAllText(fixture.Operation.ResultPath + ".stderr"));
        var line = Assert.Single(File.ReadAllLines(fixture.LedgerPath));
        using var ledger = JsonDocument.Parse(line);
        Assert.Equal("remote", ledger.RootElement.GetProperty("placement").GetString());
        Assert.Equal("mcg-exec-1", ledger.RootElement.GetProperty("executor_id").GetString());
        Assert.Equal(fixture.Operation.Id, ledger.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal(before, Snapshot(fixture.Operation.WorktreeRoot));
        Assert.True(fixture.Handle.IsAbandoned);
        Assert.False(File.Exists(RemoteExecutorHealthLedger.ResolveStorePath(fixture.Operation.WorktreeRoot)));
        using var next = fixture.Coordinator("next-attempt");
        Assert.NotNull(next.TryClaimIdleExecutor());
    }

    internal static string Git(string root, params string[] args)
    {
        var result = GitCli.Run(root, args);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output.Trim();
    }

    internal static (string Head, string Refs, string Status, string Index) Snapshot(string root)
    {
        var index = Git(root, "rev-parse", "--path-format=absolute", "--git-path", "index");
        return (Git(root, "rev-parse", "HEAD"), Git(root, "for-each-ref"), Git(root, "status", "--porcelain"),
            Convert.ToBase64String(File.ReadAllBytes(index)));
    }

    internal sealed class Fixture
    {
        internal const string Pass = "PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=1";
        internal readonly CompletionBuildEvidenceFixture Host = new();
        internal readonly Clock Time;
        internal readonly CompletionBuildEvidenceOperation Operation;
        internal readonly FakeRemoteLaneExecutor Executor = new();
        internal readonly FakeRemoteLaneExecutor.Handle Handle;
        internal RemoteLaneExecutorConfiguration Configuration;
        internal string ReceiptPath => Host.ReceiptPath;
        internal string LedgerPath => Path.Combine(Host.Root, "remote-build-check.jsonl");
        internal int LocalCalls;
        internal WorkerProcessRunResult LocalOutput = new(0, Pass, "");
        internal bool LocalReceipt = true;
        internal Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep> GitRunner = RemoteBuildCandidateCommit.RunGit;

        internal Fixture()
        {
            Host.Runner().RefreshLatestProcess(Host.Kernel, Host.GoalId, Host.TaskId);
            Time = new(Host.Clock.UtcNow);
            Operation = Host.Task.CompletionBuildEvidence! with
            {
                ResultPath = Path.Combine(Host.Root, "placement.result.json"),
                Deadline = Time.GetUtcNow() + OrchestratorBuildEvidenceCheck.DefaultTimeout + TimeSpan.FromMinutes(5)
            };
            Assert.Equal(CompletionBuildEvidencePhase.Pending, Operation.Phase);
            Assert.Equal(new[] { "src/Feature/Feature.csproj" }, Operation.Projects);
            Assert.NotEqual("", Git(Operation.WorktreeRoot, "status", "--porcelain"));
            Configuration = new([new("mcg-exec-1", 3600)], [], null);
            Handle = new(Time.GetUtcNow());
        }

        internal RemoteLaneCoordinator Coordinator(string attempt) => new(Configuration,
            new RemoteLaneCandidateIdentity(attempt, Operation.GoalId, "commit", "tree", "", "worker-build-check:v1"),
            Operation.WorktreeRoot, null, Executor, Time, TimeSpan.FromSeconds(1), null,
            claimClass: RemoteExecutorClaimClass.FocusedEvidence);

        internal void Publish(string? fault = null)
        {
            Executor.Submit = (request, _) =>
            {
                var output = Path.Combine(Host.Root, "build-output.txt");
                var receipt = Path.Combine(Host.Root, "remote-receipt.json");
                File.WriteAllText(output, fault == "red" ? "FAIL build: 1 error(s)" : Pass);
                var projects = fault == "incomplete" ? Array.Empty<string>() : Operation.Projects;
                File.WriteAllText(receipt, JsonSerializer.Serialize(new
                {
                    schemaVersion = fault == "schema" ? 2 : 1, buildOutcome = "success",
                    treeDigest = fault == "digest" ? "other" : Operation.TreeDigest,
                    projects, projectResults = projects.Select(p => new { project = p, exitCode = fault == "project-red" ? 1 : 0 })
                }));
                if (fault == "missing") File.Delete(receipt);
                Handle.Publish(new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                    fault == "tree" ? "other-tree" : request.CandidateTreeSha, request.MainSha, request.ManifestIdentity,
                    fault == "red" ? 1 : 0, [], output, receipt));
                return Task.FromResult(new RemoteLaneSubmission(Handle));
            };
        }

        internal CompletionBuildPlacementResult Local(TimeSpan budget)
        {
            LocalCalls++;
            Assert.True(budget >= OrchestratorBuildEvidenceCheck.DefaultTimeout, "Remote attempt consumed the local budget.");
            return CompletionBuildPlacement.RunLocal(Operation, ReceiptPath, budget, () =>
            {
                if (LocalReceipt) Host.WriteReceipt(Operation.Projects);
                else File.Delete(ReceiptPath);
                return LocalOutput;
            });
        }

        internal CompletionBuildPlacementResult Run(RemoteWorkerBuildCheckSettings settings,
            CompletionBuildEvidenceOperation? operation = null) => CompletionBuildPlacement.Run(operation ?? Operation,
                ReceiptPath, LedgerPath, settings,
                request => RemoteWorkerBuildCheck.RunAsync(request, new(Configuration, Executor, Time, TimeSpan.FromSeconds(1), GitRunner)), Local, Time);
    }

    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private long _ticks = now.UtcTicks;
        private readonly ManualStewardTimeProvider _timers = new();
        internal TaskCompletionSource PollTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _timers.CreateTimer(callback, state, dueTime, period);
            if (dueTime <= TimeSpan.FromSeconds(1)) PollTimerCreated.TrySetResult();
            return timer;
        }
        internal void Advance(TimeSpan elapsed) { Interlocked.Add(ref _ticks, elapsed.Ticks); _timers.Advance(elapsed); }
    }
}
