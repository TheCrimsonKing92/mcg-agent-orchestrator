using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

public sealed class CliCommandResourceHelperCoverageTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    // Explicit classification, checked against the complete declared helper surface. A new
    // helper must be classified here even if it was accidentally introduced as static.
    private static readonly HashSet<string> ResourceHelpers = new(StringComparer.Ordinal)
    {
        "CreateIsolatedCleanupContext", "CreateRefinedWorkspace", "ExecuteCliAndCapture",
        "ExecuteCliAndCaptureResult", "AssertHelpCommandDoesNotResolveGoal", "SnapshotFiles",
        "RunAppCommand", "WritePlanningBacklog", "WriteCompilationBacklog",
        "WriteConflictingCompilationBacklog", "MakeLeaseOwnerStale", "DirtyGoalWorktree",
        "EnsureGitRepository", "RecordRunningProcess", "WriteIdentityBoundHeartbeat",
        "CreateAcceptanceRepository", "CreateShortAcceptanceRepository", "CommitGoalWork",
        "CleanupAcceptanceRepository", "RunAppCli", "RunAppCliWithExitTimeout", "RunGit", "RunGitOutput"
    };

    private static readonly HashSet<string> NonResourceHelpers = new(StringComparer.Ordinal)
    {
        "ClearWorkerSandboxEnv", "BacklogItemFor", "NormalizeVolatileTimes", "SubscriptionPlanner",
        "SubscriptionDeveloper", "ReadyBlockedLines", "CountLinesContaining", "CountNonEmptyLines",
        "SingleLineContaining", "TestAgent", "AssignedAgentId", "WithGoalStatus", "BuildTaskStatusProjectionJson"
    };

    [Fact]
    public async Task EveryDeclaredResourceHelperWaitsAtTheSingleAcquirePointBeforeAccess()
    {
        var helpers = typeof(CliCommandTestBase).GetMethods(
                BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name != nameof(CliCommandTestBase.InitializeAsync) &&
                !method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .ToArray();
        Assert.Equal(ResourceHelpers.Concat(NonResourceHelpers).Order(StringComparer.Ordinal),
            helpers.Select(method => method.Name).Distinct().Order(StringComparer.Ordinal));

        foreach (var method in helpers.Where(method => ResourceHelpers.Contains(method.Name)))
        {
            Assert.False(method.IsStatic, $"{method} cannot own an instance lease while static.");
            var budget = new HostCapacityTestBudget(1, _ => null);
            var held = await budget.EnterAsync();
            var probe = new CliCommandSlotProbe(budget);
            await probe.InitializeAsync();
            // Poison arguments intentionally stop being relevant: disposal while acquisition
            // is queued must reject the invocation before it can touch any resource or argument.
            // A bypass completes/faults instead of reaching AcquisitionRequested and fails below.
            var invocation = Task.Run(async () =>
            {
                try
                {
                    var result = method.Invoke(probe, method.GetParameters()
                        .Select(parameter => parameter.ParameterType.IsValueType
                            ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray());
                    if (result is Task task)
                        await task;
                }
                catch (TargetInvocationException exception) when (exception.InnerException is not null)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                }
            });

            try
            {
                var reached = await Task.WhenAny(probe.AcquisitionRequested.Task, invocation).WaitAsync(HangGuard);
                Assert.True(reached == probe.AcquisitionRequested.Task, $"{method} bypassed acquisition.");
                Assert.False(invocation.IsCompleted, $"{method} accessed resources before a slot was available.");
                Assert.Equal(1, probe.AcquisitionRequests);
                Assert.Equal(0, budget.AvailableSlots);
            }
            finally
            {
                var disposal = probe.DisposeAsync().AsTask();
                await held.DisposeAsync();
                try
                {
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => invocation.WaitAsync(HangGuard));
                }
                finally
                {
                    await disposal.WaitAsync(HangGuard);
                }
            }

            Assert.Equal(1, budget.AvailableSlots);
            await probe.DisposeAsync();
            Assert.Equal(1, budget.AvailableSlots);
        }
    }
}
