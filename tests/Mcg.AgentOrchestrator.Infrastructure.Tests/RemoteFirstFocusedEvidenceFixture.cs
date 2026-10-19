using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

public abstract class RemoteFirstFocusedEvidenceFixture : GoalAcceptanceVerifierTestBase
{
    internal const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    internal Scenario CreateScenario() => new(this);

    internal static Check FocusedCheck(string name = "AlphaTests", string[]? keys = null, bool buildChange = false) => new()
    {
        Name = "reviewer focused evidence: " + name, Type = "dotnet-test", Project = Project,
        Runner = "mtp", Arguments = ["--filter", "FullyQualifiedName~" + name], IsFocusedEvidenceSelection = true,
        TimeoutMinutes = 1, ExclusiveResourceKeys = keys ?? [], RequiresBuildSystemChange = buildChange,
        FocusedEvidenceSelections = [[new(name, name, GoalAcceptanceVerifier.FocusedEvidenceTokenKind.Class, name, name)]]
    };

    internal static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }

    private static int StableSlotIndex(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(path, @"(?:slot-|build-)(?<slot>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups["slot"].Value, System.Globalization.CultureInfo.InvariantCulture) :
            throw new InvalidOperationException("Expected build-pool path: " + path);
    }

    private static bool IsMtpExecutableCall(string[] args, string projectName) => args.Length > 1 &&
        args[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) && args[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileNameWithoutExtension(args[1]).Equals(projectName, StringComparison.OrdinalIgnoreCase);

    private static string GetArtifactsPath(string[] args)
    {
        var index = Array.IndexOf(args, "--artifacts-path");
        Assert.InRange(index, 0, args.Length - 2);
        return args[index + 1];
    }

    private static void WriteMtpTrx(string[] args, int executed, IReadOnlyList<string> identities)
    {
        var directoryIndex = Array.IndexOf(args, "--results-directory");
        var fileIndex = Array.IndexOf(args, "--report-trx-filename");
        Assert.InRange(directoryIndex, 0, args.Length - 2);
        Assert.InRange(fileIndex, 0, args.Length - 2);
        Directory.CreateDirectory(args[directoryIndex + 1]);
        var definitions = identities.Select((identity, index) => new XElement("UnitTest",
            new XAttribute("id", "test-" + index), new XAttribute("name", identity),
            new XElement("TestMethod", new XAttribute("className", identity[..identity.LastIndexOf('.')]),
                new XAttribute("name", identity[(identity.LastIndexOf('.') + 1)..]))));
        var results = identities.Select((identity, index) => new XElement("UnitTestResult",
            new XAttribute("testId", "test-" + index), new XAttribute("testName", identity), new XAttribute("outcome", "Passed")));
        new XDocument(new XElement("TestRun", new XElement("TestDefinitions", definitions), new XElement("Results", results),
            new XElement("ResultSummary", new XAttribute("outcome", "Completed"), new XElement("Counters",
                new XAttribute("total", Math.Max(1, executed)), new XAttribute("executed", executed), new XAttribute("passed", executed),
                new XAttribute("failed", 0), new XAttribute("notExecuted", 0)))))
            .Save(Path.Combine(args[directoryIndex + 1], args[fileIndex + 1]));
    }

    internal sealed class Scenario : IDisposable
    {
        private readonly RemoteFirstFocusedEvidenceFixture _owner;
        internal string Root { get; }
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal Clock Clock { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, ".orchestrator", "executors.json");
        internal string LedgerPath => RemoteExecutorHealthLedger.ResolveStorePath(Root);
        internal string Prefix => Path.Combine(Root, ".orchestrator", "focused-local");
        internal string Head { get; }
        internal string Tree { get; }
        internal ConcurrentQueue<string> Progress { get; } = new();
        private int _localCalls;
        private int _buildCalls;
        internal int LocalCalls => Volatile.Read(ref _localCalls);
        internal int BuildCalls => Volatile.Read(ref _buildCalls);
        internal GoalAcceptanceVerifierTestOverrides Overrides => _owner.TestOverrides;

        internal Scenario(RemoteFirstFocusedEvidenceFixture owner, string? manifestText = null)
        {
            _owner = owner;
            Root = CreateManifestWorkspace(manifestText ?? """
                {"version":1,"engine":{"mtpInvocations":[{
                    "project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate":"bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate":"bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments":["{executable}","--results-directory","{resultsDirectory}","--report-trx-filename","{trxFileName}"]
                }]},"checks":[],"forbiddenChangedPathGlobs":[]}
                """);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, Project))!);
            File.WriteAllText(Path.Combine(Root, Project), "<Project />");
            File.WriteAllText(Path.Combine(Root, Path.GetDirectoryName(Project)!, "AlphaTests.cs"),
                "public class AlphaTests { [Xunit.Fact] public void Executes() { } }");
            File.WriteAllText(Path.Combine(Root, ".gitignore"), ".orchestrator/\n");
            Git("init", "-b", "main");
            Git("add", ".");
            Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
            Head = Git("rev-parse", "HEAD").Trim();
            Tree = Git("rev-parse", "HEAD^{tree}").Trim();
            Assert.Empty(Git("status", "--porcelain", "--untracked-files=all"));
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
            Overrides.RemoteLaneExecutorConfigurationPathForTests = ConfigurationPath;
            Overrides.RemoteLaneTimeProviderForTests = Clock;
            Overrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
            Overrides.OnRemoteLaneProgressLineForTests = Progress.Enqueue;
            Configure();
        }

        private string Git(params string[] args)
        {
            var result = GitCli.Run(Root, args);
            Assert.True(result.Succeeded, result.Error);
            return result.Output;
        }

        internal void Configure(string mode = "remote-first", int slots = 1, int lease = 60, int grace = 300, int sample = 9999) =>
            File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(new
            {
                executors = new[] { new { id = "fixture-executor", leaseSeconds = lease, slots } }, lanes = Array.Empty<string>(),
                machineLocalResourceKeys = new[] { "machine-local" }, focusedEvidence = new { mode, sampleEvery = sample, graceSeconds = grace }
            }));

        internal FakeRemoteLaneExecutor CompletedExecutor(bool red = false, Func<RemoteLaneResult, RemoteLaneResult>? change = null,
            int executed = 3)
        {
            var fake = new FakeRemoteLaneExecutor
            {
                Submit = (request, _) => Task.FromResult(Completion(request, red, change, executed))
            };
            Overrides.RemoteLaneExecutorForTests = fake;
            return fake;
        }

        internal RemoteLaneSubmission Completion(RemoteLaneRequest request, bool red = false,
            Func<RemoteLaneResult, RemoteLaneResult>? change = null, int executed = 3)
        {
            var handle = new FakeRemoteLaneExecutor.Handle(Clock.GetUtcNow());
            string[] args = ["--results-directory", Path.Combine(Root, ".orchestrator", "remote"), "--report-trx-filename", request.Lane + ".trx"];
            var selection = request.Filter.Split('~').Last();
            WriteMtpTrx(args, 3, [selection + ".RemoteFailure", selection + ".Second", selection + ".Third"]);
            var path = Path.Combine(args[1], args[3]);
            var trx = XDocument.Load(path);
            var counters = trx.Descendants("Counters").Single();
            counters.SetAttributeValue("executed", executed);
            if (red)
            {
                trx.Descendants("UnitTestResult").First().SetAttributeValue("outcome", "Failed");
                counters.SetAttributeValue("failed", 1);
                counters.SetAttributeValue("passed", 2);
            }
            trx.Save(path);
            var result = new RemoteLaneResult(request.ExecutorId, request.Lane, request.FilterHash,
                request.VerifyingCommitSha, request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, red ? 1 : 0, [path]);
            handle.Publish(change?.Invoke(result) ?? result);
            return new(handle);
        }

        internal Task<RemoteFirstFocusedEvidence.Outcome> DirectAsync(IReadOnlyList<Check> checks, bool keepGoing = false,
            Func<IReadOnlyList<Check>, Task<IReadOnlyList<AcceptanceCheckResult>>>? local = null, CancellationToken token = default) =>
            DirectOwnedAsync(checks, keepGoing, local, token);

        private async Task<RemoteFirstFocusedEvidence.Outcome> DirectOwnedAsync(IReadOnlyList<Check> checks, bool keepGoing,
            Func<IReadOnlyList<Check>, Task<IReadOnlyList<AcceptanceCheckResult>>>? local, CancellationToken token)
        {
            await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(Root, Goal, null,
                options: new(RunId: "direct-attempt", ResultsPrefix: Prefix));
            return await RemoteFirstFocusedEvidence.RunAsync(Overrides, Clock, Root, checks, Goal,
                (IAcceptanceRunExecutionContext)owner, keepGoing, local ?? Local, token);
        }

        private Task<IReadOnlyList<AcceptanceCheckResult>> Local(IReadOnlyList<Check> checks)
        {
            Interlocked.Add(ref _localCalls, checks.Count);
            return Task.FromResult<IReadOnlyList<AcceptanceCheckResult>>(checks.Select(check =>
                new AcceptanceCheckResult(check.Name, true, 0, "local-passed")).ToArray());
        }

        internal async Task<FocusedEvidenceRunResult> RunAsync(string attempt = "selected-attempt")
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(Root, Goal, slot,
                options: new(RunId: attempt, ResultsPrefix: Prefix));
            var verifier = new GoalAcceptanceVerifier(Overrides, Runner);
            return await verifier.RunFocusedEvidenceOwnedAsync(Root, Goal, "Infrastructure.Tests: AlphaTests", owner, slot, lease);
        }

        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                Interlocked.Increment(ref _localCalls);
                WriteMtpTrx(args, 1, ["AlphaTests.Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                Interlocked.Increment(ref _buildCalls);
                const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                var output = Path.Combine(GetArtifactsPath(args), "bin", projectName, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, projectName + ".dll"), "fixture");
                File.WriteAllText(Path.Combine(output, projectName + ".exe"), "fixture");
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        }

        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() => File.Exists(LedgerPath) ? RemoteExecutorHealthLedger.ReadAll(LedgerPath) : [];
        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }

    internal sealed class Clock : TimeProvider
    {
        private readonly ManualRemoteLaneClock _clock = new();
        private readonly ManualStewardTimeProvider _timers = new();
        internal TaskCompletionSource TimerCreated => _timers.TimerCreated;
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timers.CreateTimer(callback, state, dueTime, period);
        internal void Advance(TimeSpan elapsed) { _clock.Advance(elapsed); _timers.Advance(elapsed); }
    }
}
