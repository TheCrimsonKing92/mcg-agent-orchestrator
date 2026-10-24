using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalRefinementTestsLaunchFailureCadence
{
    [Fact]
    public void ThrownLaunchWaitsOutCadenceAndEscalates()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-refinement-launch-" + Guid.NewGuid().ToString("N"));
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var goalId = GoalId.New();
        var launches = 0;
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        GoalRefinementWorkCoordinator.UtcNowOverride = () => now;
        GoalRefinementWorkCoordinator.LaunchOverride = (_, _) =>
        {
            launches++;
            return new(false, null, "executor-launch-failed:Win32Exception:synthetic", LaunchThrew: true);
        };
        try
        {
            var first = GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId);
            Assert.True(first.LaunchThrew);
            Assert.False(first.Started);
            Assert.Equal(1, launches);
            var store = SpecRefinementLaunchAttemptStore.ForWorkspace(workspace);
            Assert.Equal(now, store.Get(goalId)!.LastLaunchAt);
            Assert.Equal(0, store.Get(goalId)!.ConsecutiveFailedClaims);

            now = now.AddMinutes(1);
            var deferred = GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId);
            Assert.Equal(GoalRefinementWorkCoordinator.CadenceDeferredDetail, deferred.Detail);
            Assert.False(deferred.Started);
            Assert.Equal(1, launches);

            for (var attempt = 2; attempt <= GoalRefinementWorkCoordinator.ConsecutiveFailedClaimLimit; attempt++)
            {
                now += GoalRefinementWorkCoordinator.RecoveryLaunchCadence;
                Assert.True(GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId).LaunchThrew);
                Assert.Equal(attempt, launches);
                Assert.Equal(now, store.Get(goalId)!.LastLaunchAt);
                Assert.Equal(attempt - 1, store.Get(goalId)!.ConsecutiveFailedClaims);
            }

            now += GoalRefinementWorkCoordinator.RecoveryLaunchCadence;
            var escalated = GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId);
            Assert.False(escalated.Started);
            Assert.Equal(GoalRefinementWorkCoordinator.ConsecutiveFailedClaimLimit, launches);
            Assert.Equal(now, store.Get(goalId)!.EscalatedAt);
            Assert.Equal(GoalRefinementWorkCoordinator.ConsecutiveFailedClaimLimit, store.Get(goalId)!.ConsecutiveFailedClaims);
            now += GoalRefinementWorkCoordinator.RecoveryLaunchCadence;
            Assert.Equal(escalated.Detail, GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId).Detail);
            Assert.Equal(GoalRefinementWorkCoordinator.ConsecutiveFailedClaimLimit, launches);

            var events = Directory.GetFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
                .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            var escalation = Assert.Single(events.Where(entry => entry.GetProperty("eventType").GetString() == "GoalEscalated"));
            Assert.Equal(goalId.Value, escalation.GetProperty("goalId").GetString());
            Assert.Equal("spec-refinement-launch", escalation.GetProperty("source").GetString());
        }
        finally
        {
            GoalRefinementWorkCoordinator.LaunchOverride = null;
            GoalRefinementWorkCoordinator.UtcNowOverride = null;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeferredLaunchStillRelaunchesImmediately()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-refinement-deferred-" + Guid.NewGuid().ToString("N"));
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var goalId = GoalId.New();
        var launches = 0;
        GoalRefinementWorkCoordinator.UtcNowOverride = () => new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        GoalRefinementWorkCoordinator.LaunchOverride = (_, _) => ++launches == 1
            ? new(false, null, "executor-launch-deferred-outside-app-host")
            : new(true, 1, "test-launch");
        try
        {
            var first = GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId);
            Assert.False(first.Started);
            Assert.False(first.LaunchThrew);
            Assert.Null(SpecRefinementLaunchAttemptStore.ForWorkspace(workspace).Get(goalId));
            var second = GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId);
            Assert.True(second.Started);
            Assert.Equal(2, launches);
        }
        finally
        {
            GoalRefinementWorkCoordinator.LaunchOverride = null;
            GoalRefinementWorkCoordinator.UtcNowOverride = null;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
