using System.Threading.Channels;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SshRemoteLaneExecutorRealTransportTests
{
    [OptInRemoteLaneSshExecutorFact]
    [Xunit.Trait("Category", "HostIntegration")]
    public async Task CheckoutHeadRunsConfigurationLaneAndReportsItsOwnTree()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var admin = Environment.GetEnvironmentVariable("MCG_REMOTE_LANE_SSH_EXECUTOR")!;
        var shaResult = GitCli.Run(root, "rev-parse", "HEAD");
        Assert.Equal(0, shaResult.ExitCode);
        var sha = shaResult.Output.Trim();
        var treeResult = GitCli.Run(root, "rev-parse", sha + "^{tree}");
        Assert.Equal(0, treeResult.ExitCode);
        var configured = RemoteLaneExecutorConfiguration.Load(RemoteLaneExecutorConfiguration.ResolveStorePath(root));
        Assert.True(configured.Enabled, configured.DisabledReason);
        var configuredEntry = Assert.Single(configured.Executors.Where(entry => entry.Transport == "ssh" && entry.AdminAlias == admin));
        var entry = configuredEntry with { RunnerAlias = admin + "-runner" };
        var folder = InfrastructureTestSupport.CreateTempDirectory();
        SshRemoteLaneHandle? handle = null;
        var polls = Channel.CreateUnbounded<SshPollObservation>();
        try
        {
            const string filter = "FullyQualifiedName~RemoteLaneExecutorConfigurationTests";
            var request = new RemoteLaneRequest(entry.Id, Guid.NewGuid().ToString("N"), "ssh-smoke",
                "infrastructure tests: Remote configuration smoke",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                filter, GoalAcceptanceVerifier.ShortHash(filter), sha, treeResult.Output.Trim(), sha, "ssh-smoke");
            var executor = new SshRemoteLaneExecutor(new([entry], [request.Lane], null), root,
                Path.Combine(folder, "attempt", "result"), TimeProvider.System,
                (args, directory, bound, token) => GoalAcceptanceVerifier.RunProcessForTestsAsync(args, directory, bound, token),
                GitCli.Run, null, observation => polls.Writer.TryWrite(observation));
            var submission = await executor.SubmitAsync(request, default);
            Assert.Null(submission.FailureReason);
            handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
            async Task<RemoteLaneResult> AwaitTerminalPoll()
            {
                while (true)
                {
                    await polls.Reader.ReadAsync();
                    if (handle.TryGetResult() is { } completed) return completed;
                }
            }
            RemoteLaneResult result;
            try { result = await AwaitTerminalPoll().WaitAsync(TimeSpan.FromMinutes(15)); }
            catch (TimeoutException) { throw new TimeoutException("Missing event: real ssh terminal poll completed"); }
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(sha, result.VerifyingCommitSha);
            Assert.NotEmpty(result.ObservedTreeSha);
            Assert.Equal(treeResult.Output.Trim(), result.ObservedTreeSha);
            Assert.Equal(request.ExecutorId, result.ExecutorId);
            Assert.Equal(request.Lane, result.Lane);
            Assert.Equal(request.FilterHash, result.FilterHash);
            Assert.Equal(request.MainSha, result.MainSha);
            Assert.Equal(request.ManifestIdentity, result.ManifestIdentity);
            Assert.NotEmpty(result.TestResultPaths);
            Assert.All(result.TestResultPaths, path => Assert.True(File.Exists(path)));
            // Keep the fetched TRX with the operator's executor-parity receipts.
            var evidence = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(root), ".orchestrator",
                "operator-evidence", "executor-parity", request.AttemptId);
            Directory.CreateDirectory(evidence);
            foreach (var path in result.TestResultPaths) File.Copy(path, Path.Combine(evidence, Path.GetFileName(path)));
        }
        finally
        {
            if (handle is not null) { handle.Abandon(); await handle.PollLoop; }
            Directory.Delete(folder, true);
        }
    }
}
