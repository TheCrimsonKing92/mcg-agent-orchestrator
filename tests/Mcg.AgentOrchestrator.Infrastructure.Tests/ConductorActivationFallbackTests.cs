using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorActivationFallbackTests
{
    [Xunit.Fact]
    public async Task ChangedActualSchemaNeverLaunchesIncompatibleFallback()
    {
        var root = Directory.CreateTempSubdirectory("fallback-schema-").FullName;
        try
        {
            var storeDirectory = Path.Combine(root, "stores");
            var portfolio = Path.Combine(storeDirectory, "portfolio.db");
            PortfolioStore.Setup(portfolio);
            var events = new RecordingRunEventStore();
            var host = new ScriptedHost((request, index, _) =>
            {
                if (index == 0)
                {
                    ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("max-duration", 1, 1, true));
                    return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
                }
                Assert.Equal(1, index); // A third request would restore the incompatible retained reader.
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = portfolio, Pooling = false
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE store_schema_versions SET version = 99";
                Assert.Equal(1, command.ExecuteNonQuery());
                return Task.FromResult(new ConductorSupervisorProcessResult(-1, 101));
            });
            var supervisor = new ConductorContinuitySupervisor(host, events,
                stageSuccessor: _ => Build("future", "future", new MemoryStream()), activationHealthyTicks: 3,
                inspectStores: () => StoreSetupRunner.Plan(storeDirectory),
                verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));
            Assert.Equal(1, await supervisor.RunAsync(["conduct", "--loop"], root, Path.Combine(root, "continuity"), "default", "default"));
            Assert.Equal(2, host.Requests.Count);
            Assert.DoesNotContain(events.Events, evt => evt.Operation == "activation" && evt.Status == "reverted");
            Assert.Contains(events.Events, evt => evt.Detail?.Contains("fallbackSuppressed=true", StringComparison.Ordinal) == true);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public async Task PreflightFailureRetainsBuildAndReleasesSuccessorLease(int failure)
    {
        var root = Directory.CreateTempSubdirectory("preflight-retained-").FullName;
        try
        {
            var events = new RecordingRunEventStore();
            using var successorLease = new MemoryStream();
            var host = new ScriptedHost((request, index, _) =>
            {
                Assert.InRange(index, 0, 1);
                if (index == 1)
                {
                    Assert.False(successorLease.CanRead);
                    Assert.DoesNotContain("--require-current-stores", request.Arguments);
                }
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new(index == 0 ? "max-duration" : "stop-file", 1, 1, index == 0));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 100 + index));
            });
            var inspections = 0;
            var supervisor = new ConductorContinuitySupervisor(host, events,
                stageSuccessor: _ => Build("future", "future", successorLease),
                inspectStores: failure == 0 ? null : () =>
                {
                    inspections++;
                    if (failure == 1) throw new IOException("fixture inspection unavailable");
                    throw new InvalidOperationException("fixture inspection invalid");
                },
                verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));

            Assert.Equal(0, await supervisor.RunAsync(["conduct", "--loop"], root,
                Path.Combine(root, "continuity"), "default", "default"));
            Assert.Equal(2, host.Requests.Count);
            Assert.Equal(host.Requests[0].CommandPrefix, host.Requests[1].CommandPrefix);
            Assert.Equal(failure == 0 ? 0 : 1, inspections);
            var failed = Assert.Single(events.Events, evt => evt.Detail?.Contains("phase=preflight", StringComparison.Ordinal) == true);
            Assert.Equal("failed", failed.Status);
            Assert.Contains("LOOP_HANDOFF_FAILED", failed.Detail);
            Assert.Contains("activationSuppressed=true", failed.Detail);
            if (failure != 0) Assert.Contains("reason=", failed.Detail);
            Assert.DoesNotContain(events.Events, evt => evt.Operation == "activation" && evt.Status == "adopted");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public async Task FailedSuccessorRestoresPriorStagedBuildAndSuppressesSameHead()
    {
        var events = new RecordingRunEventStore();
        using var priorLease = new MemoryStream();
        using var failedLease = new MemoryStream();
        using var suppressedLease = new MemoryStream();
        var prior = Build("head-0", "prior", priorLease);
        var failed = Build("head-1", "failed", failedLease);
        var suppressed = Build("head-1", "failed-again", suppressedLease);
        var stages = new Queue<ConductorPreparedSuccessor>([prior, failed, suppressed]);
        var host = new ScriptedHost((request, index, _) =>
        {
            if (index is 1 or 2 or 3)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                var ticks = index == 2 ? 1 : 3;
                for (var tick = 1; tick <= ticks; tick++)
                {
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
                }
            }

            if (index == 2)
            {
                return Task.FromResult(new ConductorSupervisorProcessResult(-1, 102));
            }

            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact(index == 4 ? "stop-file" : "max-duration",
                    3, 1, RestartRequested: index != 4));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 100 + index));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)], stageSuccessor: _ => stages.Dequeue(), activationHealthyTicks: 3,
            activationDelay: (_, _) => throw new InvalidOperationException("No deadline should be needed"),
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));

        var result = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-{Guid.NewGuid():N}"), "default", "default");

        Assert.Equal(0, result);
        Assert.Equal(5, host.Requests.Count);
        Assert.Equal(prior.AppDllPath, host.Requests[3].CommandPrefix![1]);
        Assert.Equal(prior.AppDllPath, host.Requests[4].CommandPrefix![1]);
        Assert.DoesNotContain(host.Requests.Skip(3), request =>
            request.CommandPrefix is { Count: > 1 } && request.CommandPrefix[1] == failed.AppDllPath);
        var reverted = Assert.Single(events.Events.Where(evt => evt.Operation == "activation" && evt.Status == "reverted"));
        using var payload = JsonDocument.Parse(reverted.PayloadJson!);
        Assert.Equal("head-1", payload.RootElement.GetProperty("failedBuild").GetProperty("commitSha").GetString());
        Assert.Equal("head-0", payload.RootElement.GetProperty("restoredBuild").GetProperty("commitSha").GetString());
        Assert.Contains("reason=SuccessorExited", reverted.Detail, StringComparison.Ordinal);
        Assert.Contains(events.Events, evt => evt.Operation == "activation" && evt.Status == "suppressed");
        Assert.Throws<ObjectDisposedException>(() => failedLease.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => suppressedLease.ReadByte());
    }

    [Xunit.Fact]
    public async Task RestoredBuildFailureEscalatesWithoutAlternating()
    {
        var events = new RecordingRunEventStore();
        var attention = new List<string>();
        var prepared = Build("bad-head", "bad", new MemoryStream());
        var host = new ScriptedHost((request, index, _) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 200));
            }

            if (index == 1)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
            }

            return Task.FromResult(new ConductorSupervisorProcessResult(-1, 200 + index));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)], stageSuccessor: _ => prepared, activationHealthyTicks: 3,
            activationDelay: (_, _) => throw new InvalidOperationException("No deadline should be needed"),
            raiseActivationAttention: attention.Add,
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));

        var result = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-both-{Guid.NewGuid():N}"), "default", "default");

        Assert.Equal(1, result);
        Assert.Equal(3, host.Requests.Count);
        Assert.Contains(events.Events, evt => evt.Operation == "activation" && evt.Status == "failed-both");
        Assert.Contains("bad-head", Assert.Single(attention), StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, "ReadinessNeverReported")]
    [Xunit.InlineData(true, "TickStall")]
    public async Task DeadlineBeforeHealthyThresholdRestoresPriorBuild(
        bool startsLoop, string expectedReason)
    {
        var events = new RecordingRunEventStore();
        var deadlineCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lease = new MemoryStream();
        var prepared = Build("head-timeout", "timed-out", lease);
        var host = new ScriptedHost((request, index, cancellationToken) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 300));
            }

            if (index == 1)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                if (startsLoop)
                {
                    request.OnStandardOutputLine("LOOP_START tick=0");
                    request.OnStandardOutputLine("TICK_END tick=1 activation=true");
                }

                var stopped = new TaskCompletionSource<ConductorSupervisorProcessResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => stopped.TrySetResult(
                    new ConductorSupervisorProcessResult(-1, 301, TerminationConfirmed: true)));
                return stopped.Task;
            }

            Assert.Null(request.CommandPrefix);
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact("stop-file", 1, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 302));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)], stageSuccessor: _ => prepared,
            activationDelay: (_, _) =>
            {
                deadlineCreated.TrySetResult();
                return releaseDeadline.Task;
            },
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));

        var run = supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-deadline-{Guid.NewGuid():N}"),
            "default", "default");
        await deadlineCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseDeadline.TrySetResult();
        var result = await run.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Equal(3, host.Requests.Count);
        var reverted = Assert.Single(events.Events.Where(evt =>
            evt.Operation == "activation" && evt.Status == "reverted"));
        Assert.Contains($"reason={expectedReason}", reverted.Detail, StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => lease.ReadByte());
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task CleanExitWithoutDeliberateStopBeforeThresholdReverts(bool writeArtifact)
    {
        var events = new RecordingRunEventStore();
        using var lease = new MemoryStream();
        var successor = Build("head-clean", "clean", lease);
        var host = new ScriptedHost((request, index, _) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, true));
            }
            else if (index == 1)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                request.OnStandardOutputLine("TICK_END tick=1 activation=true");
                if (writeArtifact)
                {
                    ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                        new ConductorContinuityExitArtifact("", 1, 0, false));
                }
            }
            else
            {
                Assert.Null(request.CommandPrefix);
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("stop-file", 1, 0, false));
            }

            return Task.FromResult(new ConductorSupervisorProcessResult(0, 400 + index));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)], stageSuccessor: _ => successor, activationHealthyTicks: 3,
            activationDelay: (_, _) => throw new InvalidOperationException("No deadline should be needed"),
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));

        var result = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-clean-{Guid.NewGuid():N}"),
            "default", "default");

        Assert.Equal(0, result);
        Assert.Equal(3, host.Requests.Count);
        var reverted = Assert.Single(events.Events.Where(evt =>
            evt.Operation == "activation" && evt.Status == "reverted"));
        Assert.Contains("reason=SuccessorExited", reverted.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(events.Events, evt => evt.Operation == "activation" && evt.Status == "adopted");
        Assert.Throws<ObjectDisposedException>(() => lease.ReadByte());
    }

    [Xunit.Theory]
    [Xunit.InlineData("stop-file", false, true)]
    [Xunit.InlineData("max-duration", true, true)]
    [Xunit.InlineData("stop-file", false, false)]
    public async Task RecordedDeliberateStopBeforeThresholdUsesExistingStopHandling(
        string stopReason, bool restartRequested, bool reportsReady)
    {
        var events = new RecordingRunEventStore();
        using var lease = new MemoryStream();
        var successor = Build("head-stopped", "stopped", lease);
        var stageCount = 0;
        var host = new ScriptedHost((request, index, _) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, true));
            }
            else if (index == 1)
            {
                if (reportsReady)
                {
                    request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                    request.OnStandardOutputLine("LOOP_START tick=0");
                    request.OnStandardOutputLine("TICK_END tick=1 activation=true");
                }
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact(stopReason, 1, 1, restartRequested));
            }
            else
            {
                Assert.Equal(successor.AppDllPath, request.CommandPrefix![1]);
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("stop-file", 1, 0, false));
            }

            return Task.FromResult(new ConductorSupervisorProcessResult(0, 500 + index));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)], stageSuccessor: _ => ++stageCount == 1
                ? successor
                : throw new InvalidOperationException("No newer HEAD"),
            activationHealthyTicks: 3,
            activationDelay: (_, _) => throw new InvalidOperationException("No deadline should be needed"),
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));

        var result = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-stop-{Guid.NewGuid():N}"),
            "default", "default");

        Assert.Equal(0, result);
        Assert.Equal(restartRequested ? 3 : 2, host.Requests.Count);
        Assert.DoesNotContain(events.Events, evt => evt.Operation == "activation" &&
            evt.Status is "reverted" or "adopted");
        Assert.Contains(events.Events, evt => evt.Operation == (restartRequested ? "restart" : "stopped") &&
            evt.Status == (restartRequested ? "planned" : "completed") && evt.Detail.Contains(stopReason));
    }

    [Xunit.Fact]
    public async Task IdleCompletedTicksCrossPriorDeadlineWithoutRevert()
    {
        var events = new RecordingRunEventStore();
        var adopted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        events.OnAppend = evt =>
        {
            if (evt.Operation == "activation" && evt.Status == "adopted")
                adopted.TrySetResult();
        };
        var firstDeadlineCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDeadlineCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdDeadlineCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDeadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDeadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdDeadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successorExit = new TaskCompletionSource<ConductorSupervisorProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ConductorSupervisorProcessRequest? successorRequest = null;
        var successor = Build("head-idle", "idle", new MemoryStream());
        var host = new ScriptedHost((request, index, _) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 600));
            }

            successorRequest = request;
            request.OnStandardOutputLine!("LOOP_READY lock=acquired");
            request.OnStandardOutputLine("LOOP_START tick=0");
            return successorExit.Task;
        });
        var deadlineCalls = 0;
        var supervisor = new ConductorContinuitySupervisor(host, events,
            inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)], stageSuccessor: _ => successor, activationHealthyTicks: 3,
            activationDelay: (_, _) =>
            {
                return ++deadlineCalls switch
                {
                    1 => SignalDeadline(firstDeadlineCreated, firstDeadline),
                    2 => SignalDeadline(secondDeadlineCreated, secondDeadline),
                    3 => SignalDeadline(thirdDeadlineCreated, thirdDeadline),
                    _ => throw new InvalidOperationException("Unexpected activation deadline")
                };
            },
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));

        var run = supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-idle-{Guid.NewGuid():N}"),
            "default", "default");
        await firstDeadlineCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
        successorRequest!.OnStandardOutputLine!("TICK_SUMMARY tick=1");
        successorRequest.OnStandardOutputLine("TICK_END tick=1 activation=true");
        await secondDeadlineCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
        successorRequest.OnStandardOutputLine("TICK_END tick=1 activation=true");
        secondDeadline.TrySetResult();
        await thirdDeadlineCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(adopted.Task.IsCompleted);
        successorRequest.OnStandardOutputLine("TICK_END tick=1 activation=true");
        await adopted.Task.WaitAsync(TestContext.Current.CancellationToken);
        ConductorContinuityExitArtifact.Write(successorRequest.ExitArtifactPath,
            new ConductorContinuityExitArtifact("stop-file", 3, 0, false));
        successorExit.TrySetResult(new ConductorSupervisorProcessResult(0, 601));

        Assert.Equal(0, await run.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, host.Requests.Count);
        Assert.DoesNotContain(events.Events, evt => evt.Operation == "activation" && evt.Status == "reverted");
        Assert.Single(events.Events.Where(evt => evt.Operation == "activation" && evt.Status == "adopted"));
    }

    private static Task SignalDeadline(TaskCompletionSource created, TaskCompletionSource deadline)
    {
        created.TrySetResult();
        return deadline.Task;
    }

    private static ConductorPreparedSuccessor Build(string head, string name, IDisposable lease) =>
        new($"C:\\{name}", $"C:\\{name}\\Mcg.AgentOrchestrator.App.dll", head, head,
            "LOOP_START selfCheck=true", lease);

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
