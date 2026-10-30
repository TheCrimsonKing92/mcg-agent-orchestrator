using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every process, build, event store and run directory belongs to this fixture.
public sealed class ProjectConductorHomeBuildHandoffTests
{
    [Xunit.Fact]
    public async Task ProjectSupervisorLaunchesHomeDllAndRecordsAdoptionWithoutStaging()
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
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new("self-relaunch-handoff", 1, 0, true, HomeBuildTarget: fixture.Candidate));
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
