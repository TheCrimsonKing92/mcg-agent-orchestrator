using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: git, configuration, ledger and occupancy all live in a private temp root.
public sealed class RemoteLaneToolsMismatchClassificationTests
{
    [Theory]
    [InlineData("shadow", "executor-tools-mismatch", "executor-tools-mismatch", null)]
    [InlineData("shadow", "executor-tools-unverified", "executor-tools-unverified", "executor")]
    [InlineData("shadow", "executor-busy", "executor-busy", null)]
    [InlineData("remote-first", "executor-tools-mismatch", "executor-tools-mismatch", null)]
    [InlineData("remote-first", "executor-tools-unverified", "executor-tools-unverified", "executor")]
    [InlineData("remote-first", "executor-busy", "executor-busy", null)]
    public async Task FocusedRefusal_RecordsSpecificOutcomeAndFaultOwner(
        string mode, string reason, string outcome, string? owner)
    {
        using var fixture = new FocusedFixture();
        var row = await fixture.RunAsync(mode, reason);
        Assert.Equal(outcome, row.GetProperty("outcome").GetString());
        Assert.Equal(owner, row.GetProperty("fault_owner").GetString());
        if (owner is null) Assert.Equal(JsonValueKind.Null, row.GetProperty("fault_owner").ValueKind);
        if (mode == "shadow")
        {
            Assert.True(row.GetProperty("evidence_shadow").GetBoolean());
            Assert.Equal("remote-" + outcome, row.GetProperty("agreement_reason").GetString());
            Assert.Equal(reason, row.GetProperty("reason").GetString());
        }
        else Assert.Equal("remote-first-fallback: " + reason, row.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task UnknownShadowRefusal_PreservesEveryUnreachableBaselineField()
    {
        using var fixture = new FocusedFixture();
        // The existing fake's default refusal supplies the unchanged unreachable control.
        var baseline = await fixture.RunAsync("shadow");
        var row = await fixture.RunAsync("shadow", "fixture-unreachable");
        Assert.Equal("unreachable", row.GetProperty("outcome").GetString());
        Assert.Equal("executor", row.GetProperty("fault_owner").GetString());
        Assert.Equal("remote-unreachable", row.GetProperty("agreement_reason").GetString());
        Assert.True(row.GetProperty("evidence_shadow").GetBoolean());
        static IEnumerable<(string, string)> Fields(JsonElement value) => value.EnumerateObject()
            .Where(property => property.Name != "observed_at")
            .Select(property => (property.Name, property.Value.GetRawText()));
        Assert.Equal(Fields(baseline), Fields(row));
    }

    [Theory]
    [InlineData("executor-tools-mismatch", "ExecutorToolsMismatch", null)]
    [InlineData("executor-tools-unverified", "ExecutorToolsUnverified", "executor")]
    [InlineData("executor-busy", "ExecutorBusy", null)]
    [InlineData("fixture-unreachable", "Unreachable", "executor")]
    public async Task GateRefusalRecordsSpecificOutcomeAndFaultOwner(string reason, string code, string? owner)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var clock = new ManualRemoteLaneClock();
            var entry = new RemoteLaneExecutorEntry("one", 60);
            var fake = new FakeRemoteLaneExecutor { Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, reason)) };
            using var coordinator = new RemoteLaneCoordinator(new([entry], ["lane"], null),
                new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"), root, Path.Combine(root, "result"),
                fake, clock, TimeSpan.FromSeconds(1), null);
            Assert.Equal(entry, coordinator.TryClaimIdleExecutor());
            var outcome = await coordinator.RunRemoteAsync(new Check
            {
                Name = "lane", Type = "dotnet-test",
                Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                Arguments = ["--filter", "FullyQualifiedName~FixtureTests"]
            }, entry, TimeSpan.FromMinutes(1), default);
            Assert.Null(outcome.Accepted);
            Assert.Single(fake.Requests);
            var path = RemoteExecutorHealthLedger.ResolveStorePath(root);
            var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(path));
            Assert.Equal(Enum.Parse<RemoteLaneOutcomeCode>(code), row.Outcome);
            Assert.Equal(owner, row.FaultOwner);
            using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
            Assert.Equal(JsonNamingPolicy.KebabCaseLower.ConvertName(code), json.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(owner, json.RootElement.GetProperty("fault_owner").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("executor-tools-mismatch", 1, 1.0)]
    [InlineData("unreachable", 2, 0.5)]
    [InlineData("executor-tools-unverified", 2, 0.5)]
    public void ReportExcludesOnlyVerifiedMismatchFromHealthAttempts(string outcome, int attempts, double share)
    {
        var now = new ManualRemoteLaneClock().GetUtcNow();
        var report = RemoteExecutorReport.Build(["one"],
            [new(now, "one", "accepted-attempt", "lane", "accepted", null),
             new(now, "one", "refused-attempt", "lane", outcome, null)], []);
        var row = Assert.Single(report.Executors);
        Assert.Equal(attempts, row.Attempts);
        Assert.Equal(share, row.AcceptedShare);
    }

    private sealed class FocusedFixture : IDisposable
    {
        private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
        private readonly GoalId _goal = new(Guid.NewGuid().ToString("N"));
        private readonly ManualRemoteLaneClock _clock = new();
        private readonly string _head;
        private readonly string _tree;

        internal FocusedFixture()
        {
            Directory.CreateDirectory(Path.Combine(_root, "config"));
            File.WriteAllText(Path.Combine(_root, "config", "acceptance-manifest.json"),
                "{\"version\":1,\"engine\":{},\"checks\":[],\"forbiddenChangedPathGlobs\":[]}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, Project))!);
            File.WriteAllText(Path.Combine(_root, Project), "<Project />");
            File.WriteAllText(Path.Combine(_root, Path.GetDirectoryName(Project)!, "AlphaTests.cs"),
                "public class AlphaTests { [Xunit.Fact] public void Executes() { } }");
            File.WriteAllText(Path.Combine(_root, ".gitignore"), ".orchestrator/\n");
            Git("init", "-b", "main");
            Git("add", ".");
            Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
                "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
            _head = Git("rev-parse", "HEAD").Trim();
            _tree = Git("rev-parse", "HEAD^{tree}").Trim();
            Assert.Empty(Git("status", "--porcelain", "--untracked-files=all"));
        }

        internal async Task<JsonElement> RunAsync(string mode, string? refusal = null)
        {
            var configuration = Path.Combine(_root, ".orchestrator", "executors.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configuration)!);
            File.WriteAllText(configuration, JsonSerializer.Serialize(new
            {
                executors = new[] { new { id = "one" } }, lanes = Array.Empty<string>(),
                focusedEvidence = new { mode, sampleEvery = 1, graceSeconds = 300 }
            }));
            var ledger = RemoteExecutorHealthLedger.ResolveStorePath(_root);
            File.Delete(ledger);
            var fake = new FakeRemoteLaneExecutor();
            if (refusal is not null) fake.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, refusal));
            var overrides = new GoalAcceptanceVerifierTestOverrides
            {
                RemoteLaneExecutorConfigurationPathForTests = configuration,
                RemoteLaneTimeProviderForTests = _clock,
                RemoteLaneExecutorForTests = fake
            };
            var check = new Check
            {
                Name = "reviewer focused evidence: AlphaTests", Type = "dotnet-test", Project = Project,
                Runner = "mtp", Arguments = ["--filter", "FullyQualifiedName~AlphaTests"],
                IsFocusedEvidenceSelection = true, TimeoutMinutes = 1
            };
            var local = new AcceptanceCheckResult(check.Name, true, 0, "local-passed");
            await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(_root, _goal, null,
                options: new(RunId: "fixture-attempt", ResultsPrefix: Path.Combine(_root, ".orchestrator", "focused-local")));
            var context = (IAcceptanceRunExecutionContext)owner;
            if (mode == "shadow")
            {
                await using var shadow = RemoteFocusedEvidenceShadow.Start(overrides, _clock, _root,
                    [check], _goal, context, TestContext.Current.CancellationToken);
                await shadow.CompleteAsync([local]);
            }
            else
            {
                var localCalls = 0;
                var result = await RemoteFirstFocusedEvidence.RunAsync(overrides, _clock, _root, [check], _goal,
                    context, false, checks =>
                    {
                        Assert.Same(check, Assert.Single(checks));
                        localCalls++;
                        return Task.FromResult<IReadOnlyList<AcceptanceCheckResult>>([local]);
                    }, TestContext.Current.CancellationToken);
                Assert.Same(local, Assert.Single(result.Results));
                Assert.Equal(1, localCalls);
            }
            var request = Assert.Single(fake.Requests);
            Assert.Equal("one", request.ExecutorId);
            Assert.Equal(_head, request.VerifyingCommitSha);
            Assert.Equal(_tree, request.CandidateTreeSha);
            Assert.Equal(_head, request.MainSha);
            using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(ledger)));
            return json.RootElement.Clone();
        }

        private string Git(params string[] arguments)
        {
            var result = GitCli.Run(_root, arguments);
            Assert.True(result.Succeeded, result.Error);
            return result.Output;
        }

        public void Dispose()
        {
            // Git object files are read-only on Windows; clear that attribute before deletion.
            foreach (var entry in new DirectoryInfo(_root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                entry.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(_root, recursive: true);
        }
    }
}
