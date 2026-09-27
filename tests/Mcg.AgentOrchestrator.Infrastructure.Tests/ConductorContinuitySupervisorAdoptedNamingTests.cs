using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorContinuitySupervisorAdoptedNamingTests
{
    [Xunit.Fact]
    public async Task AdoptionNamesCandidateAndPreviousBuilds()
    {
        var events = new RecordingRunEventStore();
        var conductLines = new List<string>();
        var stages = new Queue<ConductorPreparedSuccessor>([
            Build("head-0", "first"),
            Build("head-1", "second")
        ]);
        var finalExit = new TaskCompletionSource<ConductorSupervisorProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ConductorSupervisorProcessRequest? finalRequest = null;
        var adoptedCount = 0;
        events.OnAppend = evt =>
        {
            if (evt.Operation != "activation" || evt.Status != "adopted")
                return;
            if (++adoptedCount != 2)
                return;
            ConductorContinuityExitArtifact.Write(finalRequest!.ExitArtifactPath,
                new ConductorContinuityExitArtifact("stop-file", 3, 0, false));
            finalExit.TrySetResult(new ConductorSupervisorProcessResult(0, 102));
        };
        var host = new ScriptedHost((request, index, _) =>
        {
            if (index > 0)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                for (var tick = 1; tick <= 3; tick++)
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
            }

            if (index == 2)
            {
                finalRequest = request;
                return finalExit.Task;
            }

            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact("max-duration", 3, 1, true));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 100 + index));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            stageSuccessor: _ => stages.Dequeue(), activationHealthyTicks: 3,
            activationDelay: (_, _) => throw new InvalidOperationException("No deadline should be needed"),
            appendConductEvent: (_, _, line) => conductLines.Add(line));

        var result = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-adopted-naming-{Guid.NewGuid():N}"),
            "default", "default", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Equal(3, host.Requests.Count);
        var adopted = events.Events.Where(evt => evt.Operation == "activation" && evt.Status == "adopted").ToArray();
        Assert.Equal(2, adopted.Length);
        Assert.DoesNotContain(events.Events, evt => evt.Operation == "activation" && evt.Status == "reverted");
        var replacement = adopted[1];
        Assert.Contains("adoptedCommit=head-1", replacement.Detail, StringComparison.Ordinal);
        Assert.Contains("previousCommit=head-0", replacement.Detail, StringComparison.Ordinal);
        Assert.Contains(conductLines, line => line.Contains("ACTIVATION_ADOPTED adoptedCommit=head-1", StringComparison.Ordinal) &&
            line.Contains("previousCommit=head-0", StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(replacement.PayloadJson!);
        Assert.Equal("head-1", payload.RootElement.GetProperty("adoptedBuild").GetProperty("commitSha").GetString());
        Assert.Equal("head-0", payload.RootElement.GetProperty("previousBuild").GetProperty("commitSha").GetString());
    }

    [Xunit.Fact]
    public async Task SuppressionNamesSuppressedAndCurrentBuilds()
    {
        var events = new RecordingRunEventStore();
        var stages = new Queue<ConductorPreparedSuccessor>([
            Build("head-0", "prior"),
            Build("head-1", "failed"),
            Build("head-1", "suppressed")
        ]);
        var host = new ScriptedHost((request, index, _) =>
        {
            if (index is 1 or 2 or 3)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                var ticks = index == 2 ? 1 : 3;
                for (var tick = 1; tick <= ticks; tick++)
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
            }

            if (index == 2)
                return Task.FromResult(new ConductorSupervisorProcessResult(-1, 102));

            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact(index == 4 ? "stop-file" : "max-duration",
                    3, 1, RestartRequested: index != 4));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 100 + index));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            stageSuccessor: _ => stages.Dequeue(), activationHealthyTicks: 3,
            activationDelay: (_, _) => throw new InvalidOperationException("No deadline should be needed"));

        var result = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-suppressed-naming-{Guid.NewGuid():N}"),
            "default", "default", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        var suppressed = Assert.Single(events.Events.Where(evt =>
            evt.Operation == "activation" && evt.Status == "suppressed"));
        Assert.Contains("suppressedCommit=head-1", suppressed.Detail, StringComparison.Ordinal);
        Assert.Contains("currentCommit=head-0", suppressed.Detail, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(suppressed.PayloadJson!);
        Assert.Equal("head-1", payload.RootElement.GetProperty("suppressedBuild").GetProperty("commitSha").GetString());
        Assert.Equal("head-0", payload.RootElement.GetProperty("currentBuild").GetProperty("commitSha").GetString());
    }

    private static ConductorPreparedSuccessor Build(string head, string name) =>
        new($"C:\\{name}", $"C:\\{name}\\Mcg.AgentOrchestrator.App.dll", head, head,
            "LOOP_START selfCheck=true", new MemoryStream());

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
        : IConductorSupervisorProcessHost
    {
        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            return step(request, index, cancellationToken);
        }
    }

    private sealed class RecordingRunEventStore : IRunEventStore
    {
        public List<RunEventAppend> Events { get; } = [];
        public Action<RunEventAppend>? OnAppend { get; set; }

        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            OnAppend?.Invoke(evt);
            return Task.FromResult(new RunEventRecord(Events.Count, evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId, evt.Operation,
                evt.Status, evt.Detail, evt.PayloadJson));
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
