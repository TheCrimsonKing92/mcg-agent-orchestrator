using System.ComponentModel;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case uses an in-memory goal and injected process, clock and I/O seams.
public sealed class WatchProgressProcessLabelTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(101, 202, 303)]
    [InlineData(101, 303, 202)]
    [InlineData(202, 101, 303)]
    [InlineData(202, 303, 101)]
    [InlineData(303, 101, 202)]
    [InlineData(303, 202, 101)]
    public void LiveIdentities_VariedOrder_LabelFirstOwnerGenerically(int first, int second, int third)
    {
        var lines = Render(Heartbeat([first, second, third]));

        AssertOwnedProcess(lines, first);
    }

    [Theory]
    [InlineData(@"C:\workers\claude.exe")]
    [InlineData(@"C:\workers\wrapper.exe")]
    [InlineData(@"C:\Windows\System32\conhost.exe")]
    [InlineData(@"C:\workers\neutral.exe")]
    public void ImageName_WithoutRoleEvidence_DoesNotAuthorizeWorkerLabel(string imagePath)
    {
        var identity = Identity(101) with { ImagePath = imagePath };
        var heartbeat = Heartbeat([101]) with { OwnedProcessIdentities = [identity] };

        var lines = Render(heartbeat, readIdentity: _ => identity);

        AssertOwnedProcess(lines, 101);
    }

    [Theory]
    [InlineData(202)]
    [InlineData(303)]
    public void ModelExited_HelperAlive_ReportsOnlyOwnedProcessLiveness(int helper)
    {
        var lines = Render(Heartbeat([101, helper]), isAlive: pid => pid == helper);

        AssertOwnedProcess(lines, helper);
        Assert.DoesNotContain(lines, line => line.Contains("pid=101", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing-identities")]
    [InlineData("unavailable-heartbeat")]
    [InlineData("reused-pid")]
    [InlineData("image-mismatch")]
    [InlineData("unreadable-identity")]
    [InlineData("inaccessible-liveness")]
    [InlineData("inaccessible-identity")]
    [InlineData("all-dead")]
    public void InvalidOwnerEvidence_PreservesUnknownPidAndNoLiveState(string scenario)
    {
        var heartbeat = Heartbeat([101]);
        if (scenario == "missing-identities")
        {
            heartbeat = heartbeat with { OwnedProcessIdentities = [] };
        }
        else if (scenario == "unavailable-heartbeat")
        {
            heartbeat = heartbeat with { IsAvailable = false };
        }

        var lines = Render(heartbeat,
            isAlive: _ => scenario switch
            {
                "all-dead" => false,
                "inaccessible-liveness" => throw new Win32Exception(5),
                _ => true
            },
            readIdentity: pid => scenario switch
            {
                "reused-pid" => Identity(pid) with { StartedAt = StartedAt.AddMinutes(1) },
                "image-mismatch" => Identity(pid) with { ImagePath = @"C:\other.exe" },
                "unreadable-identity" => null,
                "inaccessible-identity" => throw new Win32Exception(5),
                _ => Identity(pid)
            });

        AssertUnknownProcess(lines, "NO LIVE WORKER");
        Assert.Single(lines, line => line.StartsWith("WATCH_WARNING ", StringComparison.Ordinal) &&
            line.Contains("reason=no-live-worker", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InaccessibleCandidate_LaterOwnerAlive_StillDisplaysLaterOwner(bool identityProbe)
    {
        var lines = Render(Heartbeat([101, 202]),
            isAlive: pid => pid == 101 && !identityProbe ? throw new Win32Exception(5) : true,
            readIdentity: pid => pid == 101 && identityProbe ? throw new Win32Exception(5) : Identity(pid));

        AssertOwnedProcess(lines, 202);
    }

    [Fact]
    public void CompletedProcess_NoLiveOwner_PreservesExitingState()
    {
        var lines = Render(Heartbeat([101]), isAlive: _ => false, completed: true);

        AssertUnknownProcess(lines, "exiting");
        Assert.DoesNotContain(lines, line => line.StartsWith("WATCH_WARNING ", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingProcess_PreservesUnknownPidAndNoLiveState()
    {
        var lines = Render(Heartbeat([101]), missingProcess: true);

        AssertUnknownProcess(lines, "NO LIVE WORKER");
    }

    private static void AssertOwnedProcess(IReadOnlyList<string> lines, int pid)
    {
        var (machine, human) = ProgressLines(lines);
        Assert.Contains($" pid={pid} liveness=\"alive\" ", machine);
        Assert.Contains($"owned process pid={pid} alive", human);
        Assert.DoesNotContain("worker pid=", human, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model worker", human, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(lines, line => line.StartsWith("WATCH_WARNING ", StringComparison.Ordinal));
    }

    private static void AssertUnknownProcess(IReadOnlyList<string> lines, string liveness)
    {
        var (machine, human) = ProgressLines(lines);
        Assert.Contains($" pid=unknown liveness=\"{liveness}\" ", machine);
        Assert.Contains($"owned process pid=unknown {liveness}", human);
        Assert.DoesNotContain("worker pid=", human, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Machine, string Human) ProgressLines(IReadOnlyList<string> lines) =>
        (Assert.Single(lines, line => line.StartsWith("WATCH_PROGRESS ", StringComparison.Ordinal)),
         Assert.Single(lines, line => line.StartsWith("[", StringComparison.Ordinal) &&
             line.Contains("(task ", StringComparison.Ordinal)));

    private static SpawnProcessIdentity Identity(int pid) => new(pid, StartedAt, pid switch
    {
        101 => @"C:\workers\claude.exe",
        202 => @"C:\workers\wrapper.exe",
        303 => @"C:\Windows\System32\conhost.exe",
        _ => @"C:\workers\host.exe"
    });

    private static DispatchHeartbeatStatus Heartbeat(IReadOnlyList<int> ownedPids) =>
        new("heartbeat.json", true, null, 404, 101, ownedPids, "running",
            StartedAt.AddMinutes(1), StartedAt.AddMinutes(1), TimeSpan.Zero, TimeSpan.Zero, 0, 0)
        {
            OwnedProcessIdentities = ownedPids.Select(Identity).ToArray()
        };

    private static IReadOnlyList<string> Render(
        DispatchHeartbeatStatus heartbeat,
        Func<int, bool>? isAlive = null,
        Func<int, SpawnProcessIdentity?>? readIdentity = null,
        bool completed = false,
        bool missingProcess = false)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, AgentCatalog.Default().Agents, "Report owned process liveness");
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("worker", "run", @"C:\repo", StartedAt));
        if (!missingProcess)
        {
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                new TaskProcessRecord(404, "run", @"C:\repo", "worker.out.log", "worker.err.log",
                    "worker.exit.txt", StartedAt, completed ? StartedAt.AddSeconds(30) : null,
                    completed ? 0 : null, OwnedProcessIds: heartbeat.OwnedProcessIds));
        }

        var reporter = new ConductorWatchProgressReporter(
            readHeartbeat: (_, _) => heartbeat,
            readChanges: (_, _) => new DispatchLiveChangeSnapshot([], [], 0),
            isProcessAlive: isAlive ?? (_ => true),
            readProcessIdentity: readIdentity ?? Identity,
            now: () => StartedAt.AddMinutes(1),
            loopProcessId: 9001,
            launcherProcessId: 9002);

        return reporter.BuildLines(kernel.GetGoal(goal.Id), false, ConductorAutonomyPolicy.Conservative);
    }
}
