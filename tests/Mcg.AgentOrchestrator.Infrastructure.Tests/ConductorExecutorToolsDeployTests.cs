using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorExecutorToolsDeployTests
{
    [Fact]
    public async Task MismatchCopiesThenSwapsWithEverySlotReservedAndOneExactEvent()
    {
        using var fixture = new Fixture();
        var conductor = fixture.Create();
        await Run(conductor);
        Assert.Equal(new[] { "read", "copy", "swap" }, fixture.Calls);
        Assert.Equal(new[] { $"EXECUTOR_TOOLS_DEPLOY executor=one outcome=deployed old={Fixture.A} new={Fixture.B} main={Fixture.S[..12]} backup=tools.bak-20261006T000000Z" }, fixture.Events());
        var script = Assert.IsType<string>(fixture.Script);
        foreach (var text in new[] { "Get-ExecutorToolsDigest.ps1", "runner-state.json", "tools.bak-20261006T000000Z" }) Assert.Contains(text, script);
        fixture.AssertFree();
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await Run(conductor);
        Assert.Equal(new[] { "read", "copy", "swap" }, fixture.Calls);
        Assert.Single(fixture.Events());
    }

    [Fact]
    public async Task EqualDigestReadsOnlyOnceWithNoEvent()
    {
        using var fixture = new Fixture { Deployed = Fixture.B };
        await Run(fixture.Create());
        Assert.Equal(new[] { "read" }, fixture.Calls);
        Assert.Equal(Array.Empty<string>(), fixture.Events());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task IneligibleConfigurationEqualsEmptyBaseline(bool check, bool admin)
    {
        using var fixture = new Fixture();
        fixture.Configure(check, admin);
        await Run(fixture.Create());
        Assert.Equal(Array.Empty<string>(), fixture.Calls);
        Assert.Equal(Array.Empty<string>(), fixture.Events());
        Assert.Equal(0, fixture.SourceReads);
    }

    [Fact]
    public async Task OccupiedExecutorHasNoTransportOrEvent()
    {
        using var fixture = new Fixture();
        RemoteExecutorOccupancy.Claim(fixture.Root, "one", "gate");
        try
        {
            Assert.True(RemoteExecutorOccupancy.IsOccupied(fixture.Root, "one"));
            await Run(fixture.Create());
            Assert.Equal(Array.Empty<string>(), fixture.Calls);
            Assert.Equal(Array.Empty<string>(), fixture.Events());
        }
        finally { RemoteExecutorOccupancy.Release(fixture.Root, "one", "gate"); }
    }

    [Fact]
    public async Task PartialReservationReleasesEarlierSlotsAndSkipsDeploy()
    {
        using var fixture = new Fixture();
        using (var busySlot = RemoteExecutorOccupancy.TryClaimExclusive(fixture.Root, "one", 1))
        {
            Assert.NotNull(busySlot);
            await Run(fixture.Create());
            Assert.Equal(new[] { "read" }, fixture.Calls);
            Assert.Equal(Array.Empty<string>(), fixture.Events());
            using var firstSlot = RemoteExecutorOccupancy.TryClaimExclusive(fixture.Root, "one", 0);
            Assert.NotNull(firstSlot);
            Assert.False(RemoteExecutorOccupancy.IsOccupied(fixture.Root, "one"));
        }
        fixture.AssertFree();
    }

    [Fact]
    public async Task InFlightRunReturnsImmediatelyWithoutStartingAnother()
    {
        using var fixture = new Fixture { Deployed = Fixture.B };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conductor = fixture.Create(async (args, directory, bound, token) =>
        {
            entered.TrySetResult();
            await release.Task;
            return await fixture.Transport(args, directory, bound, token);
        });
        try
        {
            conductor.Evaluate();
            await Event(entered.Task, "manifest transport entered");
            var run = conductor.CurrentRun;
            fixture.Clock.Advance(TimeSpan.FromMinutes(10));
            conductor.Evaluate();
            Assert.Same(run, conductor.CurrentRun);
            Assert.False(run!.IsCompleted);
            release.TrySetResult();
            await Event(run, "manifest run completed");
            Assert.Equal(new[] { "read" }, fixture.Calls);
            Assert.Equal(Array.Empty<string>(), fixture.Events());
        }
        finally
        {
            release.TrySetResult();
            if (conductor.CurrentRun is { } task) await Event(task, "in-flight cleanup completed");
        }
    }

    [Fact]
    public async Task TwoMinuteStartThrottleAndTenMinuteReadCadenceUseInjectedClock()
    {
        using var fixture = new Fixture { Deployed = Fixture.B };
        var conductor = fixture.Create();
        await Run(conductor);
        var first = conductor.CurrentRun;
        fixture.Clock.Advance(TimeSpan.FromSeconds(119));
        conductor.Evaluate();
        Assert.Same(first, conductor.CurrentRun);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await Run(conductor);
        Assert.NotSame(first, conductor.CurrentRun);
        Assert.Equal(new[] { "read" }, fixture.Calls);
        fixture.Clock.Advance(TimeSpan.FromMinutes(8));
        await Run(conductor);
        Assert.Equal(new[] { "read", "read" }, fixture.Calls);
        Assert.Equal(Array.Empty<string>(), fixture.Events());
    }

    [Fact]
    public async Task ChangedMainShaRefreshesManifestBeforeTenMinutes()
    {
        using var fixture = new Fixture { Deployed = Fixture.B };
        var conductor = fixture.Create();
        await Run(conductor);
        fixture.Source = fixture.Source! with { MainSha = new string('d', 40) };
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await Run(conductor);
        Assert.Equal(new[] { "read", "read" }, fixture.Calls);
        Assert.Equal(Array.Empty<string>(), fixture.Events());
    }

    [Theory]
    [InlineData("remote-runner-busy", 599)]
    [InlineData("remote-runner-busy", 600)]
    [InlineData("remote-staged-digest-mismatch", 599)]
    [InlineData("remote-staged-digest-mismatch", 600)]
    [InlineData("remote-backup-exists", 599)]
    [InlineData("remote-backup-exists", 600)]
    [InlineData("remote-swap-failed", 599)]
    [InlineData("remote-swap-failed", 600)]
    [InlineData("readback-mismatch", 599)]
    [InlineData("readback-mismatch", 600)]
    [InlineData("timed-out", 599)]
    [InlineData("timed-out", 600)]
    [InlineData("unreachable", 599)]
    [InlineData("unreachable", 600)]
    public async Task FailureRecordsExactEventReleasesSlotsAndSuppressesOnlyForTenMinutes(string reason, int elapsed)
    {
        using var fixture = new Fixture { Failure = reason };
        var conductor = fixture.Create();
        await Run(conductor);
        var expected = $"EXECUTOR_TOOLS_DEPLOY executor=one outcome=failed reason={reason} old={Fixture.A} new={Fixture.B} main={Fixture.S[..12]}";
        Assert.Equal(new[] { expected }, fixture.Events());
        Assert.Equal(new[] { "read", "copy", "swap" }, fixture.Calls);
        fixture.AssertFree();
        fixture.Clock.Advance(TimeSpan.FromSeconds(elapsed));
        await Run(conductor);
        Assert.Equal(elapsed == 599 ? new[] { "read", "copy", "swap" } : new[] { "read", "copy", "swap", "read", "copy", "swap" }, fixture.Calls);
        Assert.Equal(elapsed == 599 ? new[] { expected } : new[] { expected, expected }, fixture.Events());
        fixture.AssertFree();
    }

    [Fact]
    public async Task UnavailableMainReportsOnceUntilSourceRecovers()
    {
        using var fixture = new Fixture { Source = null };
        var conductor = fixture.Create();
        for (var round = 0; round < 3; round++)
        {
            await Run(conductor);
            fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        }
        Assert.Equal(new[] { "EXECUTOR_TOOLS_DEPLOY outcome=failed reason=main-tools-unavailable main=none" }, fixture.Events());
        Assert.Equal(Array.Empty<string>(), fixture.Calls);
        Assert.Equal(3, fixture.SourceReads);
    }

    [Fact]
    public void DeployEventsAreClassifiedAndArtifactsRegistered()
    {
        Assert.Equal("decision", ConductEventOperatorClassifier.Classify("executor-tools-deploy", "EXECUTOR_TOOLS_DEPLOY executor=one outcome=failed reason=unreachable"));
        Assert.Equal("outcome", ConductEventOperatorClassifier.Classify("executor-tools-deploy", "EXECUTOR_TOOLS_DEPLOY executor=one outcome=deployed"));
        Assert.Null(ConductEventOperatorClassifier.Classify("executor-tools-deploy", "EXECUTOR_TOOLS_DEPLOY executor=one"));
        Assert.Contains(OrchestratorArtifactRegistry.Entries, entry => entry.Pattern == "executor-tools-deploy");
    }

    private static async Task Run(ConductorExecutorToolsDeploy conductor)
    {
        conductor.Evaluate();
        await Event(Assert.IsAssignableFrom<Task>(conductor.CurrentRun), "tools deploy run completed");
    }
    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly string A = new('a', 64);
        internal static readonly string B = new('b', 64);
        internal static readonly string S = new('1', 40);
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal List<string> Calls { get; } = [];
        internal string? Script { get; private set; }
        internal string? Failure { get; init; }
        internal string? Deployed { get; init; } = A;
        internal ExecutorToolsSource? Source { get; set; }
        internal int SourceReads { get; private set; }
        private string Configuration => Path.Combine(Root, "executors.json");
        private string EventsPath => Path.Combine(Root, "events.log");
        internal Fixture()
        {
            Source = new(S, Path.Combine(Root, "tools-staging-" + B[..12]), B);
            Directory.CreateDirectory(Source.Folder);
            Configure(true, true);
        }
        internal void Configure(bool check, bool admin) => File.WriteAllText(Configuration, JsonSerializer.Serialize(new
        {
            executors = new[] { new { id = "one", transport = "ssh", runnerAlias = "runner-one", adminAlias = admin ? "admin-one" : null,
                remoteRepository = "C:/repo/bare.git", runRoot = "D:/runner", slots = 2, toolsDigestCheck = check } }, lanes = Array.Empty<string>()
        }));
        internal ConductorExecutorToolsDeploy Create(Func<string[], string, TimeSpan, CancellationToken,
            Task<GoalAcceptanceVerifier.CommandResult>>? transport = null) => new(Root, Configuration, Clock,
                transport ?? Transport, RemoteExecutorOccupancy.DefaultStartTime, new ConductEventLogWriter(EventsPath),
                () => { SourceReads++; return Source; });
        internal Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory, TimeSpan bound, CancellationToken token)
        {
            Assert.Equal(CancellationToken.None, token);
            if (args[0] == SshRemoteLaneExecutor.ScpPath && !args.Contains("-r"))
            {
                Calls.Add("read");
                Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", "admin-one:D:/runner/tools-digest.json", "tools-digest.json" }, args);
                Assert.Equal(TimeSpan.FromSeconds(30), bound);
                if (Deployed is not null) File.WriteAllText(Path.Combine(directory, args[^1]), JsonSerializer.Serialize(new { digest = Deployed }));
            }
            else
            {
                Assert.Equal(TimeSpan.FromMinutes(2), bound);
                Assert.True(RemoteExecutorOccupancy.IsOccupied(Root, "one"));
                for (var slot = 0; slot < 2; slot++)
                {
                    using var claim = RemoteExecutorOccupancy.TryClaimExclusive(Root, "one", slot);
                    Assert.Null(claim);
                }
                if (args[0] == SshRemoteLaneExecutor.ScpPath)
                {
                    Calls.Add("copy");
                    Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", "-r", "tools-staging-" + Source!.Digest[..12], "admin-one:D:/runner/" }, args);
                }
                else
                {
                    Calls.Add("swap");
                    Assert.Equal("admin-one", args[5]);
                    Assert.Equal("-EncodedCommand", args[^2]);
                    Script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
                    var backup = "tools.bak-" + Clock.GetUtcNow().ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
                    var result = Failure switch
                    {
                        "timed-out" => new GoalAcceptanceVerifier.CommandResult(-1, "", true),
                        "unreachable" => new GoalAcceptanceVerifier.CommandResult(255, "{}"),
                        { } reason when reason.StartsWith("remote-", StringComparison.Ordinal) => new(0, JsonSerializer.Serialize(new { error = reason[7..] })),
                        _ => new(0, JsonSerializer.Serialize(new { previous = Deployed, backup, readback = Failure == "readback-mismatch" ? new string('c', 64) : Source!.Digest }))
                    };
                    return Task.FromResult(result);
                }
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
        }
        internal string[] Events() => File.Exists(EventsPath) ? File.ReadAllLines(EventsPath).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal("executor-tools-deploy", json.RootElement.GetProperty("eventKind").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("goalId").ValueKind);
            return json.RootElement.GetProperty("detail").GetString()!;
        }).ToArray() : [];
        internal void AssertFree()
        {
            Assert.False(RemoteExecutorOccupancy.IsOccupied(Root, "one"));
            for (var slot = 0; slot < 2; slot++)
            {
                using var claim = RemoteExecutorOccupancy.TryClaimExclusive(Root, "one", slot);
                Assert.NotNull(claim);
            }
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
