using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every process, build, event store and run directory belongs to this fixture.
public sealed class ProjectConductorHomeBuildHandoffTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ProjectSupervisorLaunchesHomeDllAndRecordsAdoptionWithoutStaging(bool delegateRelaunch)
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var events = new RecordingEvents();
        var lines = new List<string>();
        var stagingCalls = 0;
        var exit = new TaskCompletionSource<ConductorSupervisorProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<ConductorSupervisorProcessRequest>();
        var host = new ScriptedHost(request =>
        {
            requests.Add(request);
            if (requests.Count == 1)
            {
                var summary = new BatchLoopSummary(1, 0, 0, 0, 0, 0, true,
                    StopReason: "self-relaunch-handoff") { HomeBuildTarget = fixture.Candidate };
                var artifact = ConductorContinuityExitArtifactFactory.FromLoopSummary(summary, delegateRelaunch);
                Assert.True(artifact.RestartRequested);
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, artifact);
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
            }
            Assert.Equal(2, requests.Count);
            request.OnStandardOutputLine!("LOOP_READY lock=acquired");
            request.OnStandardOutputLine("LOOP_START tick=0");
            request.OnStandardOutputLine("TICK_END tick=1 activation=true");
            return exit.Task;
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            stageSuccessor: _ => { stagingCalls++; throw new InvalidOperationException("A project must not stage home"); },
            homeBuildFollower: new HomeBuildFollowerSuccessorSource(fixture.Root),
            inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)],
            maxRenewalsWithoutProgress: 0, activationHealthyTicks: 1,
            dotnetPath: "fixture-dotnet",
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"),
            appendConductEvent: (_, _, line) =>
            {
                lines.Add(line);
                if (!line.StartsWith("ACTIVATION_ADOPTED ", StringComparison.Ordinal)) return;
                ConductorContinuityExitArtifact.Write(requests[1].ExitArtifactPath, new("stop-file", 1, 0, false));
                exit.TrySetResult(new ConductorSupervisorProcessResult(0, 101));
            });
        Assert.Equal(0, await supervisor.RunAsync(["conduct", "--loop"], fixture.Root,
            Path.Combine(fixture.Root, "project-continuity"), "project", "default")
            .WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["fixture-dotnet", fixture.Candidate.AppDllPath], requests[1].CommandPrefix);
        Assert.Contains(lines, line => line.StartsWith("ACTIVATION_ADOPTED ", StringComparison.Ordinal) &&
            line.Contains($"adoptedCommit={fixture.Candidate.AdoptedCommit}", StringComparison.Ordinal));
        Assert.Equal(0, stagingCalls);
        Assert.Equal(fixture.Candidate, HomeBuildAdoptionRecord.Read(fixture.RecordPath));
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task IneligibleRetry_AppliedUpgradeSuppressesRetainedLaunch(bool applyUpgrade, bool revert)
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var lines = new List<string>();
        var requests = new List<ConductorSupervisorProcessRequest>();
        var stateDirectory = Path.Combine(fixture.Root, "project-state");
        var database = Path.Combine(stateDirectory, "fixture.db");
        var setupCalls = 0;
        var planCalls = 0;
        var retryCalls = 0;
        var upgrade = new ConductorHandoffSchemaUpgrade(stateDirectory, fixture.Root,
            run: (dll, arguments, _) =>
            {
                Assert.Equal(fixture.Candidate.AppDllPath, dll);
                if (arguments.Contains("--plan"))
                {
                    planCalls++;
                    var version = setupCalls > 0 || !applyUpgrade ? 2 : 1;
                    var action = version == 2 ? "current" : "migrate";
                    return new(0, $"fixture: recorded {version}; required 2; action {action} ({database})", "");
                }
                Assert.Equal(new[] { "conductor", "setup", "--project=project", "--tenant=default" }, arguments);
                setupCalls++;
                return new(0, "", "");
            });
        var host = new ScriptedHost(request =>
        {
            requests.Add(request);
            Assert.InRange(requests.Count, 1, 3);
            if (requests.Count == 1)
            {
                File.WriteAllText(request.StdoutPath, "LOOP_RELAUNCH_DRAIN active=0 liveGates=0");
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new("self-relaunch-handoff", 1, 0, true, HomeBuildTarget: fixture.Candidate));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
            }
            if (requests.Count == 2)
            {
                Assert.Equal(fixture.Candidate.AppDllPath, request.CommandPrefix![1]);
                Assert.Equal(applyUpgrade ? 1 : 0, setupCalls);
                return Task.FromResult(new ConductorSupervisorProcessResult(1, 101));
            }
            Assert.False(applyUpgrade, "An upgraded store cannot be opened by the retained runtime");
            Assert.Null(request.CommandPrefix);
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 1, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 102));
        });
        var retryDelay = TimeSpan.FromSeconds(60);
        var supervisor = new ConductorContinuitySupervisor(host, new RecordingEvents(),
            homeBuildFollower: new HomeBuildFollowerSuccessorSource(fixture.Root),
            schemaUpgrade: upgrade,
            inspectStores: () => [new("fixture", database, 1, 1)],
            successorExitRetryDelay: retryDelay,
            delay: (duration, _) =>
            {
                Assert.Equal(retryDelay, duration);
                retryCalls++;
                Assert.Contains(lines, line => line.StartsWith("LOOP_HANDOFF_RETRY_SCHEDULED ", StringComparison.Ordinal));
                if (revert)
                {
                    fixture.Publish(fixture.Candidate with { Status = "reverted" });
                    Assert.Null(fixture.Detector().Detect());
                }
                else
                {
                    var latestRun = Directory.CreateDirectory(Path.Combine(fixture.Root, "latest")).FullName;
                    var latestDll = Path.Combine(latestRun, "Mcg.AgentOrchestrator.App.dll");
                    var latest = fixture.Candidate with { Sequence = 3, AdoptedCommit = new string('c', 40),
                        RunDirectory = latestRun, AppDllPath = latestDll };
                    File.WriteAllText(latestDll, "latest fixture output");
                    File.WriteAllText(latestDll + ".git-head", latest.AdoptedCommit);
                    fixture.Publish(latest);
                    Assert.Equal(latest, fixture.Detector().Detect());
                }
                return Task.CompletedTask;
            },
            appendConductEvent: (_, _, line) => lines.Add(line),
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));

        Assert.Equal(applyUpgrade ? 1 : 0, await supervisor.RunAsync(["conduct", "--loop"], fixture.Root,
            Path.Combine(stateDirectory, "continuity"), "project", "default")
            .WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(applyUpgrade ? 2 : 3, requests.Count);
        Assert.Equal(applyUpgrade ? 1 : 0, setupCalls);
        Assert.Equal(applyUpgrade ? 2 : 1, planCalls);
        Assert.Equal(1, retryCalls);
        Assert.Contains("LOOP_HANDOFF_SKIPPED reason=home-adoption-no-longer-eligible", lines);
        Assert.Equal(applyUpgrade, lines.Any(line => line.StartsWith(
            "SUPERVISOR_ESCALATED reason=fallback-incompatible-after-upgrade ", StringComparison.Ordinal)));
        using var released = File.Open(fixture.Candidate.AppDllPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Xunit.Theory]
    [Xunit.InlineData("self-relaunch-handoff", false, false)]
    [Xunit.InlineData("self-relaunch-handoff", true, true)]
    [Xunit.InlineData("max-duration", false, true)]
    [Xunit.InlineData("max-duration", true, true)]
    [Xunit.InlineData("stop-file", false, false)]
    [Xunit.InlineData("stop-file", true, false)]
    public void ExitArtifact_NoHomeTargetPreservesDelegationPolicy(string stopReason,
        bool delegateRelaunch, bool expectedRestart)
    {
        var summary = new BatchLoopSummary(1, 0, 0, 0, 0, 0, true, StopReason: stopReason);
        Assert.Equal(expectedRestart,
            ConductorContinuityExitArtifactFactory.FromLoopSummary(summary, delegateRelaunch).RestartRequested);
    }

    [Xunit.Fact]
    public void SourceRetainsExactOutputAndReleasesItOnAbandonment()
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var upgrades = 0;
        var source = new HomeBuildFollowerSuccessorSource(fixture.Root);
        var successor = source.Prepare(fixture.Candidate, fixture.Running, null, (dll, commit, _) =>
        {
            Assert.Equal(fixture.Candidate.AppDllPath, dll);
            Assert.Equal(fixture.Candidate.AdoptedCommit, commit);
            upgrades++;
        }, CancellationToken.None);
        Assert.NotNull(successor);
        try
        {
            Assert.Equal(fixture.Candidate.RunDirectory, successor.RunDirectory);
            Assert.Equal(1, upgrades);
            Assert.Throws<IOException>(() => File.Open(fixture.Candidate.AppDllPath,
                FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
            fixture.Publish(fixture.Candidate with { Status = "reverted" });
            Assert.False(source.IsEligible(successor, fixture.Running, null));
            Assert.Null(source.Prepare(fixture.Candidate, fixture.Running, null,
                (_, _, _) => throw new InvalidOperationException("Reverted output cannot upgrade"), CancellationToken.None));
        }
        finally { successor.RunDirectoryLease!.Dispose(); }
        using var released = File.Open(fixture.Candidate.AppDllPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Xunit.Fact]
    public void SourceSelectsLatestFinalBuildAndReleasesLeaseWhenUpgradeFails()
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var latest = fixture.Candidate with { Sequence = 3 };
        fixture.Publish(latest);
        var source = new HomeBuildFollowerSuccessorSource(fixture.Root);
        Assert.Throws<InvalidOperationException>(() => source.Prepare(fixture.Candidate, fixture.Running, null,
            (_, _, _) => throw new InvalidOperationException("schema upgrade failed"), CancellationToken.None));
        using var released = File.Open(fixture.Candidate.AppDllPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class ScriptedHost(Func<ConductorSupervisorProcessRequest, Task<ConductorSupervisorProcessResult>> run)
        : IConductorSupervisorProcessHost
    {
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request,
            CancellationToken cancellationToken) => run(request);
    }

    private sealed class RecordingEvents : IRunEventStore
    {
        private long _sequence;
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RunEventRecord(++_sequence, evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId,
                evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
