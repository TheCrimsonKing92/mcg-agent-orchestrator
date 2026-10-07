using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneOccupancy : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task OccupancyLastsUntilAcceptanceOrRefusedSubmissionFallback(bool accepted)
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        using var scenario = new Scenario(this);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = scenario.ConfigurationPath;
        RemoteLaneOfferSeeding.PrepareFixture(scenario.Root, TestOverrides);
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        var fake = new FakeRemoteLaneExecutor();
        var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
        var refusal = new TaskCompletionSource<RemoteLaneSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Submit = (_, _) => accepted ? Task.FromResult(new RemoteLaneSubmission(handle)) : refusal.Task;
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var gate = scenario.RunAsync();
        RemoteLaneRequest? request = null;
        try
        {
            request = await Event(fake.Submitted.Task, "remote submission started");
            if (accepted) await Event(handle.Polled.Task, "pending remote handle polled");
            var hostRoot = AcceptancePartitionVerdictCache.ResolveHostStateRoot(scenario.Root);
            Assert.True(RemoteExecutorOccupancy.IsOccupied(hostRoot, "fixture-executor"));
            if (accepted) handle.Publish(scenario.RemoteResult(request));
            else refusal.SetResult(new(null, "fixture-unreachable"));
            var result = await Event(gate, "gate completed");
            Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
            Assert.False(RemoteExecutorOccupancy.IsOccupied(hostRoot, "fixture-executor"));
            Assert.Equal(!accepted, scenario.LocalStarted);
            var health = Assert.Single(RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(scenario.Root)));
            Assert.Equal(accepted ? RemoteLaneOutcomeCode.Accepted : RemoteLaneOutcomeCode.Unreachable, health.Outcome);
        }
        finally
        {
            refusal.TrySetResult(new(null, "fixture-unreachable"));
            if (request is not null && accepted) handle.Publish(scenario.RemoteResult(request));
            await Event(gate, "gate cleanup");
        }
    }

    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
    private static async Task<T> Event<T>(Task<T> task, string name) { await Event((Task)task, name); return await task; }
    private sealed class Scenario : IDisposable
    {
        private readonly GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneOccupancy _owner;
        internal string Root { get; }
        private GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, "executors.json");
        internal bool LocalStarted { get; private set; }
        internal Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneOccupancy owner)
        {
            _owner = owner;
            Root = CreateManifestWorkspace("""
                {
                  "version":1,
                  "engine":{
                    "maxConcurrentShards":3,
                    "infrastructureTestLanes":[{"name":"Lane 0","filter":"FullyQualifiedName~Lane0Tests"}],
                    "mtpInvocations":[{
                      "project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                      "executablePathTemplate":"bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                      "firewallExecutablePathTemplate":"bin/{projectName}/{configuration}/{projectName}.exe",
                      "arguments":["{executable}","--results-directory","{resultsDirectory}","--report-trx-filename","{trxFileName}"]
                    }]
                  },
                  "checks":[{
                    "name":"infrastructure tests","type":"dotnet-test","runner":"mtp",
                    "project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "timeoutMinutes":2
                  }],
                  "forbiddenChangedPathGlobs":[]
                }
                """);
            var source = Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Lane0Tests.cs"), "public class Lane0Tests { [Xunit.Fact] public void Executes() { } }");
            File.WriteAllText(ConfigurationPath, """{"executors":[{"id":"fixture-executor"}],"lanes":["infrastructure tests: Lane 0"]}""");
        }
        internal async Task<AcceptanceVerificationResult> RunAsync()
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var verifier = new GoalAcceptanceVerifier(_owner.TestOverrides, Runner);
            var attempt = Guid.NewGuid().ToString("N");
            var prefix = Path.Combine(Root, ".orchestrator", "attempts", attempt, "result");
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity(attempt, Goal.Value, Root, "candidate-tree", "main-sha", "verifying-commit",
                    prefix, slot, Environment.ProcessId, null), AcceptanceGateEngineSettings.Load(Root), default);
            return await verifier.RunOwnedAsync(Root, Goal, null, slot, lease, executionOwner);
        }
        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                LocalStarted = true;
                WriteMtpTrx(args, 1, ["Lane0Tests.Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                const string project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                var output = Path.Combine(GetArtifactsPath(args), "bin", project, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, project + ".dll"), "deterministic fixture");
                File.WriteAllText(Path.Combine(output, project + ".exe"), "deterministic fixture");
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        }
        internal RemoteLaneResult RemoteResult(RemoteLaneRequest request)
        {
            var directory = Path.Combine(Root, "remote-receipts");
            const string filename = "remote.trx";
            WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", filename], 1, ["Lane0Tests.Executes"]);
            return new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [Path.Combine(directory, filename)]);
        }
        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }
}
