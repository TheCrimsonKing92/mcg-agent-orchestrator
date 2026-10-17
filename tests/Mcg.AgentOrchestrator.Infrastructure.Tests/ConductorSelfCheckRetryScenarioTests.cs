using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorSelfCheckProbe;

// Fixture-owned stores, build outputs and child TEMP; only git reads the real repository.
// The probe and supervisor host are scripted. Only the default-timeout case touches the new
// environment key, saving/restoring it; all other cases supply an explicit options timeout.
public sealed class ConductorSelfCheckRetryScenarioTests
{
    [Fact]
    public async Task Timeout_then_success_prepares_the_successor_and_records_one_retry()
    {
        using var fixture = new Fixture();
        string? readiness = null;
        Assert.Equal(0, await fixture.RunAsync((arguments, _, _, _) =>
        {
            if (fixture.ProbeCalls == 1) return new(-1, "", "", true);
            readiness = $"LOOP_START gitHead={arguments[4]} runDir={Path.GetDirectoryName(arguments[0])}";
            return new(0, readiness, "", false);
        }));

        var prepared = Assert.IsType<ConductorPreparedSuccessor>(fixture.Prepared);
        Assert.Equal(readiness, prepared.SelfCheckDetail);
        Assert.Equal(2, fixture.ProbeCalls);
        Assert.Single(fixture.Events, line => line.StartsWith("SELF_CHECK_RETRY reason=timeout ", StringComparison.Ordinal));
        AssertProbeAttempts(fixture.Events);
        Assert.DoesNotContain(fixture.Events, line => line.StartsWith("LOOP_HANDOFF_FAILED", StringComparison.Ordinal) && line.Contains("phase=self-check"));
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.Null(fixture.Host.Requests[0].CommandPrefix);
        Assert.Equal(new[] { "dotnet-fixture", prepared.AppDllPath }, fixture.Host.Requests[1].CommandPrefix);
        Assert.Equal(prepared.RunDirectory, Path.GetDirectoryName(prepared.AppDllPath));
    }

    [Fact]
    public async Task Two_timeouts_fall_back_to_the_incumbent_with_the_probe_output_in_the_failure_line()
    {
        using var fixture = new Fixture();
        Assert.Equal(0, await fixture.RunAsync((_, _, _, _) =>
            new(-1, "stage=open-store\nstage=load-catalog", "waiting for state lock", true)));

        Assert.Equal(2, fixture.ProbeCalls);
        Assert.Null(fixture.Prepared);
        Assert.Equal("self-check", Assert.IsType<ConductorSelfRelaunchPreparationException>(fixture.Failure).Phase);
        var failure = Assert.Single(fixture.Events, line => line.StartsWith("LOOP_HANDOFF_FAILED", StringComparison.Ordinal));
        foreach (var detail in new[] { "phase=self-check", "timedOut=True", "attempts=2", "stage=load-catalog", "waiting for state lock" })
            Assert.Contains(detail, failure);
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.Null(fixture.Host.Requests[1].CommandPrefix);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Options.AppDllPath)));
    }

    [Fact]
    public void Missing_environment_and_options_use_thirty_seconds_for_both_attempts()
    {
        var prior = Environment.GetEnvironmentVariable(SelfCheckTimeoutEnvironmentVariable);
        try
        {
            if (prior is not null) Environment.SetEnvironmentVariable(SelfCheckTimeoutEnvironmentVariable, null);
            using var fixture = new Fixture();
            Assert.Throws<ConductorSelfRelaunchPreparationException>(() =>
                fixture.Prepare((_, _, _, _) => new(-1, "", "", true), fixture.Options with { SelfCheckTimeout = default }));
            Assert.Equal(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) }, fixture.Timeouts);
        }
        finally
        {
            if (prior is not null) Environment.SetEnvironmentVariable(SelfCheckTimeoutEnvironmentVariable, prior);
        }
    }

    [Fact]
    public void Explicit_timeout_is_used_for_both_attempts()
    {
        using var fixture = new Fixture();
        Assert.Throws<ConductorSelfRelaunchPreparationException>(() =>
            fixture.Prepare((_, _, _, _) => new(-1, "", "", true), fixture.Options with { SelfCheckTimeout = TimeSpan.FromSeconds(12) }));
        Assert.Equal(new[] { TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(12) }, fixture.Timeouts);
    }

    [Fact]
    public void Successful_process_with_invalid_readiness_is_not_retried()
    {
        using var fixture = new Fixture();
        var failure = Assert.Throws<ConductorSelfRelaunchPreparationException>(() =>
            fixture.Prepare((_, _, _, _) => new(0, "LOOP_START gitHead=wrong runDir=wrong", "", false)));
        Assert.Equal("self-check", failure.Phase);
        Assert.Equal("Successor did not return the compatible LOOP_START readiness contract.", failure.Message);
        Assert.Equal(1, fixture.ProbeCalls);
        Assert.DoesNotContain(fixture.Events, line => line.StartsWith("SELF_CHECK_RETRY", StringComparison.Ordinal));
        Assert.EndsWith("outcome=ok", Assert.Single(fixture.Events));
    }

    private static void AssertProbeAttempts(List<string> events)
    {
        var elapsed = events.Where(line => line.StartsWith("SELF_CHECK_ELAPSED", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, elapsed.Length);
        Assert.StartsWith("SELF_CHECK_ELAPSED attempt=1 ms=", elapsed[0]);
        Assert.EndsWith("outcome=timeout", elapsed[0]);
        Assert.StartsWith("SELF_CHECK_ELAPSED attempt=2 ms=", elapsed[1]);
        Assert.EndsWith("outcome=ok", elapsed[1]);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("self-check-retry-").FullName;
        internal ConductorSuccessorStagingOptions Options { get; }
        internal List<string> Events { get; } = [];
        internal List<TimeSpan> Timeouts { get; } = [];
        internal int ProbeCalls { get; private set; }
        internal ConductorPreparedSuccessor? Prepared { get; private set; }
        internal ConductorSelfRelaunchPreparationException? Failure { get; private set; }
        internal Host Host { get; } = new();

        internal Fixture()
        {
            var repository = InfrastructureTestSupport.FindRepositoryRoot();
            var stateDirectory = Directory.CreateDirectory(Path.Combine(Root, ".orchestrator")).FullName;
            var state = Path.Combine(stateDirectory, "state.db");
            _ = InfrastructureTestSupport.CreateMigratedStateRepository(state).LoadAsync().GetAwaiter().GetResult();
            StoreSetupRunner.Run(stateDirectory);
            var prebuilt = Path.Combine(Root, "prebuilt");
            ConductorSelfRelaunchSharedAppPayload.CreatePrivateCopy(prebuilt);
            var isolatedTemp = Directory.CreateDirectory(Path.Combine(Root, "isolated-temp")).FullName;
            var resolver = Path.Combine(Root, "resolve-test-run-dir.ps1");
            static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
            File.WriteAllText(resolver, string.Join(Environment.NewLine,
                "param([string]$Dll)", "$ErrorActionPreference = 'Stop'",
                $"$env:TEMP = '{Quote(isolatedTemp)}'", $"$env:TMP = '{Quote(isolatedTemp)}'",
                $"& '{Quote(Path.Combine(repository, "scripts", "resolve-run-dir.ps1"))}' -Dll $Dll"));
            Options = new(repository,
                Path.Combine(repository, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
                Path.Combine(Root, "build-output", LandingAppBuildStore.AppDllName),
                Path.Combine(repository, "scripts", "Update-AppDllGitHeadMarker.ps1"), resolver, state,
                Path.Combine(stateDirectory, "agents.json"), Path.Combine(stateDirectory, "worker-profiles.json"),
                Path.Combine(stateDirectory, "model-functions.json"), "dotnet-fixture", "powershell",
                SelfCheckTimeout: TimeSpan.FromSeconds(30)) { PrebuiltAppOutputDirectory = prebuilt };
        }

        internal ConductorPreparedSuccessor Prepare(SuccessorProbeRunner runner, ConductorSuccessorStagingOptions? options = null) =>
            ConductorSelfRelaunch.PrepareSuccessor(options ?? Options, probeRunner: (args, directory, timeout, token) =>
            {
                ProbeCalls++;
                Timeouts.Add(timeout);
                return runner(args, directory, timeout, token);
            }, recordEvent: Events.Add);

        internal Task<int> RunAsync(SuccessorProbeRunner runner)
        {
            var supervisor = new ConductorContinuitySupervisor(Host, new RecordingEvents(),
                stageSuccessor: _ =>
                {
                    try { return Prepared = Prepare(runner); }
                    catch (ConductorSelfRelaunchPreparationException ex) { Failure = ex; throw; }
                }, appendConductEvent: (_, _, detail) => Events.Add(detail),
                dotnetPath: "dotnet-fixture", activationHealthyTicks: 3);
            return supervisor.RunAsync(["conduct", "--loop", "--repository-root", Root,
                    "--expected-state-directory", Path.GetDirectoryName(Options.StateStorePath)!, "--disposable-fixture"],
                Root, Path.Combine(Root, "continuity"), "fixture", "tenant");
        }

        public void Dispose()
        {
            Prepared?.RunDirectoryLease?.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class Host : IConductorSupervisorProcessHost
    {
        internal List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken token)
        {
            var index = Requests.Count;
            Requests.Add(request);
            Assert.InRange(index, 0, 1);
            if (index == 0)
            {
                File.WriteAllText(request.StdoutPath, "LOOP_RELAUNCH_DRAIN active=0 liveGates=0\nLOOP_RELAUNCH_REBUILD active=0 admitting=false");
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("self-relaunch", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
            }
            if (request.CommandPrefix is not null)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                for (var tick = 1; tick <= 3; tick++) request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
            }
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 3, 1, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 101));
        }
    }

    private sealed class RecordingEvents : IRunEventStore
    {
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RunEventRecord(1, evt.EventId ?? "fixture", evt.OccurredAt ?? DateTimeOffset.MinValue,
                evt.EventType, evt.GoalId, evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0, string? goalId = null,
            int maxCount = 500, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
