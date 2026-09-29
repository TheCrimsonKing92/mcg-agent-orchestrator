using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

[Xunit.Collection("EnvMutation")]
public sealed class DispatchHostLifetimeHandoffTestsIdentityPolicy
{
    private static readonly ConcurrentDictionary<string, int> ReservedPids = new(StringComparer.Ordinal);
    private static readonly DateTimeOffset ProcessStartedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void SuccessorDetach_RejectsStaleRegistryOwner_FromSeededRow()
    {
        var pid = ReserveNonRunningProcessId();
        var fixture = CreateSeededFixture(pid, ProcessStartedAt);
        try
        {
            var row = SeedRuntimeOwnedRow(fixture.DbPath, "unrelated-goal:unrelated-task", pid);
            var killAttempts = new List<int>();
            var runner = new BackgroundDispatchRunner(tryKillOwnedProcess: attemptedPid =>
            {
                killAttempts.Add(attemptedPid);
                return false;
            });

            Assert.Equal(0, runner.DetachRunningProcessesForGoal(fixture.Kernel, fixture.Goal.Id));

            var task = fixture.Kernel.GetTask(fixture.Goal.Id, fixture.Task.Id);
            Assert.Equal(WorkTaskStatus.Failed, task.Status);
            Assert.False(task.LastProcess!.WasGracefullyDetachedByConductor);
            Assert.Contains(fixture.Kernel.GetGoal(fixture.Goal.Id).Timeline,
                entry => entry.TaskId == fixture.Task.Id &&
                    entry.Message.Contains("durable-owner-mismatch", StringComparison.Ordinal));
            Assert.Equal(SpawnRegistryLifecycle.RuntimeOwned,
                Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests(), entry => entry.Id == row.Id).Lifecycle);
            Assert.Empty(killAttempts);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Xunit.Fact]
    public void SuccessorDetach_UpgradesLegacyIdentity_FromSeededDurableOwnerRow()
    {
        var pid = ReserveNonRunningProcessId();
        var fixture = CreateSeededFixture(pid, identityStartedAt: null);
        try
        {
            var row = SeedRuntimeOwnedRow(fixture.DbPath, fixture.OwnerId, pid);
            Assert.Null(fixture.Kernel.GetTask(fixture.Goal.Id, fixture.Task.Id).LastProcess!.ProcessIdentityStartedAt);
            var killAttempts = new List<int>();
            var runner = new BackgroundDispatchRunner(tryKillOwnedProcess: attemptedPid =>
            {
                killAttempts.Add(attemptedPid);
                return false;
            });

            Assert.Equal(1, runner.DetachRunningProcessesForGoal(fixture.Kernel, fixture.Goal.Id));

            var detached = Assert.IsType<TaskProcessRecord>(
                fixture.Kernel.GetTask(fixture.Goal.Id, fixture.Task.Id).LastProcess);
            Assert.True(detached.WasGracefullyDetachedByConductor);
            Assert.Equal(row.ProcessStartedAt, detached.ProcessIdentityStartedAt);
            Assert.Equal(SpawnRegistryLifecycle.ConductorDetached,
                Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests(), entry => entry.Id == row.Id).Lifecycle);
            Assert.Empty(killAttempts);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Xunit.Fact]
    public void SuccessorDetach_RecycledPidWithoutIdentity_RequeuesWithoutKill_FromSeededRow()
    {
        var pid = ReserveNonRunningProcessId();
        var fixture = CreateSeededFixture(pid, identityStartedAt: null);
        try
        {
            var row = SeedRuntimeOwnedRow(fixture.DbPath, fixture.OwnerId, pid);
            new SpawnRegistry(fixture.DbPath).MarkReleasedEntry(row.Id, "test: released before legacy detach");
            var recorded = Assert.IsType<TaskProcessRecord>(
                fixture.Kernel.GetTask(fixture.Goal.Id, fixture.Task.Id).LastProcess);
            Assert.Equal(pid, recorded.ProcessId);
            Assert.Null(recorded.ProcessIdentityStartedAt);
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            var killAttempts = new List<int>();
            var livenessChecks = new List<int>();
            var runner = new BackgroundDispatchRunner(
                isStillRunning: checkedPid => { livenessChecks.Add(checkedPid); return true; },
                tryKillOwnedProcess: attemptedPid => { killAttempts.Add(attemptedPid); return false; });

            Assert.Equal(0, runner.DetachRunningProcessesForGoal(fixture.Kernel, fixture.Goal.Id));

            var interrupted = fixture.Kernel.GetTask(fixture.Goal.Id, fixture.Task.Id);
            Assert.Equal(WorkTaskStatus.Cancelled, interrupted.Status);
            Assert.True(interrupted.LastProcess!.WasCancelledByConductor);
            Assert.Contains("expected-dispatch-identity-missing", interrupted.LastProcess.ExitArtifactReason);
            Assert.Contains(pid, livenessChecks);
            Assert.Empty(killAttempts);
            Assert.Equal(1, runner.RequeueInterruptedDispatches(fixture.Kernel));
            Assert.Equal(WorkTaskStatus.Assigned, fixture.Kernel.GetTask(fixture.Goal.Id, fixture.Task.Id).Status);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Xunit.Fact]
    public void IdentityDecisionFacts_UseSeededInputsOnly()
    {
        var path = ThisSourcePath();
        Assert.True(File.Exists(path), $"Identity decision source missing: {path}");
        var methods = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().ToDictionary(method => method.Identifier.ValueText);
        foreach (var name in new[]
        {
            nameof(SuccessorDetach_RejectsStaleRegistryOwner_FromSeededRow),
            nameof(SuccessorDetach_UpgradesLegacyIdentity_FromSeededDurableOwnerRow),
            nameof(SuccessorDetach_RecycledPidWithoutIdentity_RequeuesWithoutKill_FromSeededRow)
        })
        {
            var body = Assert.IsType<BlockSyntax>(methods[name].Body).ToString();
            var forbidden = ForbiddenTokens(body).ToArray();
            Assert.True(forbidden.Length == 0,
                $"{name} uses forbidden token(s): {string.Join(", ", forbidden)}");
            Assert.Equal(1, methods[name].Body!.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(call => call.Expression.ToString() == nameof(ReserveNonRunningProcessId)));
            Assert.DoesNotContain("GetProcessById", body);
            Assert.DoesNotContain("Environment.ProcessId", body);
        }

        Assert.Contains("AppCaller.Start", ForbiddenTokens("AppCaller.Start(root); Task.Delay(1);"));
        Assert.Contains("Task.Delay", ForbiddenTokens("AppCaller.Start(root); Task.Delay(1);"));
        Assert.False(IsRunning(ReserveNonRunningProcessId(nameof(IdentityDecisionFacts_UseSeededInputsOnly))));
        foreach (var pid in ReservedPids.Values)
        {
            Assert.False(IsRunning(pid), $"Reserved pid {pid} became live.");
        }
    }

    private static IEnumerable<string> ForbiddenTokens(string body)
    {
        string[] tokens = ["AppCaller", "Process.Start", "ProcessStartInfo", "UnrelatedSleeper",
            "Thread.Sleep", "Task.Delay", "SpinWait", "Stopwatch", "WaitFor", "WaitUntil", "WaitAsync"];
        return tokens.Where(token => body.Contains(token, StringComparison.Ordinal));
    }

    private static string ThisSourcePath([CallerFilePath] string sourcePath = "")
    {
        var root = VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot)
            ? Path.Combine(verifiedRoot, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests")
            : Path.GetDirectoryName(sourcePath)!;
        return Path.Combine(root, Path.GetFileName(sourcePath));
    }

    private static int ReserveNonRunningProcessId([CallerMemberName] string fact = "")
    {
        for (var candidate = int.MaxValue - 3; candidate > int.MaxValue - 1000; candidate -= 4)
        {
            if (IsRunning(candidate)) continue;
            Assert.False(WorkerProcessJobs.HasRegisteredJob(candidate));
            Assert.True(ReservedPids.TryAdd(fact, candidate), $"Fact {fact} reserved more than one pid.");
            return candidate;
        }

        throw new InvalidOperationException("No non-running process id was available for the identity decision fact.");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static SeededFixture CreateSeededFixture(int pid, DateTimeOffset? identityStartedAt)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        WorkerProcessJobs.ConfigureRegistry(workspace.SqliteStatePath);
        var agent = new AgentDefinition(
            new AgentId("lifetime-reviewer"), "Lifetime fixture Reviewer", AgentRole.Reviewer,
            new ModelProfile("OpenAI", "local-fixture", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("lifetime-fixture"));
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Inspect the deterministic local fixture.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Prove dispatch host caller lifetime handoff", [task]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "The deterministic local fixture completes.", ["The local worker returns a valid Reviewer result."],
            VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, [agent]);
        const string command = "seeded-worker-command";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "lifetime-fixture", command, root, ProcessStartedAt,
            ProviderName: "OpenAI", ModelName: "local-fixture", DispatchLane: "lifetime-fixture"));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
            pid, command, root, Path.Combine(root, "worker.out"), Path.Combine(root, "worker.err"),
            Path.Combine(root, "worker.exit"), ProcessStartedAt, null, null,
            OwnedProcessIds: null, ChildProcessId: null, NonBlockingProcessIds: null,
            ProcessIdentityStartedAt: identityStartedAt));
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        return new SeededFixture(root, workspace.SqliteStatePath, kernel, goal, task);
    }

    private static SpawnRegistryEntry SeedRuntimeOwnedRow(string dbPath, string ownerId, int pid)
    {
        var registry = new SpawnRegistry(dbPath);
        var identity = new SpawnProcessIdentity(pid, ProcessStartedAt, "seeded-worker-image");
        registry.Register(ownerId, identity, ownerIdentity: null);
        Assert.True(registry.TryMarkRuntimeOwned(identity, "spawn_registry: runtime-owned seeded"));
        return Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests(), entry => entry.ProcessId == pid);
    }

    private static void Cleanup(SeededFixture fixture)
    {
        WorkerProcessJobs.ClearRegistryForTests();
        try { Directory.Delete(fixture.Root, recursive: true); } catch { }
    }

    private sealed record SeededFixture(
        string Root, string DbPath, AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task)
    {
        public string OwnerId => $"{Goal.Id.Value}:{Task.Id.Value}";
    }
}
