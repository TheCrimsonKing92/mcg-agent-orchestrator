using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class BoardFillCliGoalIntake(OrchestratorWorkspace workspace) : IBoardFillGoalIntake
{
    public string? FindCreatedGoal(string requestKey)
    {
        var receipt = new GoalIntakeRequestStore(workspace.SqliteStatePath).Get(requestKey);
        return receipt?.State == GoalIntakeRequestStates.Created && receipt.GoalId is { } id &&
            ReadGoals().Any(goal => goal.Id.Value == id) ? id : null;
    }

    public BoardFillIntakeResult File(BoardFillIntakeRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var store = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        var before = store.Get(request.RequestKey);
        Exception? failure = null;
        try { Execute(request.Arguments); }
        catch (Exception exception) { failure = exception; }
        var receipt = store.Get(request.RequestKey);
        // Use the intake's own durable receipt, never Console.SetOut (which is process-wide).
        var stdout = receipt is null ? "" : JsonSerializer.Serialize(new
        {
            kind = "goal-intake-receipt", requestKey = receipt.RequestKey, state = receipt.State,
            createdAt = receipt.CreatedAt, updatedAt = receipt.UpdatedAt, goalId = receipt.GoalId,
            failureCode = receipt.FailureCode, failureDetail = receipt.FailureDetail,
            stdoutPath = receipt.StdoutPath, stderrPath = receipt.StderrPath
        });
        var created = receipt?.State == GoalIntakeRequestStates.Created && receipt.GoalId is not null &&
            ReadGoals().Any(goal => goal.Id.Value == receipt.GoalId);
        // A post-commit delivery exception does not undo a goal. Other faults (including payload
        // conflicts on an already-created key) must not be mistaken for a successful replay.
        if (created && (failure is null || before?.State != GoalIntakeRequestStates.Created &&
                failure.Message.StartsWith("GOAL_CREATE_DELIVERY_INCOMPLETE", StringComparison.Ordinal)))
            return new(before?.State == GoalIntakeRequestStates.Created ? "replayed" : "filed",
                receipt!.GoalId, stdout, failure?.ToString() ?? "", 0, failure is null ? "ok" : "delivery-incomplete");
        return new("failed", null, stdout, failure?.ToString() ?? receipt?.FailureDetail ?? "No completed goal intake receipt.",
            1, receipt?.State == GoalIntakeRequestStates.StillCommitting ? "intake-pending" : "intake-failed");
    }

    public BoardFillIntakeResult Depend(string goalId, string dependencyGoalId, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            Execute(["goal-depends", goalId, "--on", dependencyGoalId]);
            var applied = ReadGoals().Single(goal => goal.Id.Value == goalId)
                .DependsOn.Any(dependency => dependency.Value == dependencyGoalId);
            return new(applied ? "applied" : "failed", goalId,
                applied ? $"Dependency set: {BoardFillFiledEvent.Id8(goalId)} depends on {BoardFillFiledEvent.Id8(dependencyGoalId)}" : "",
                applied ? "" : "Dependency was not persisted.", applied ? 0 : 1);
        }
        catch (Exception exception) { return new("failed", goalId, "", exception.ToString(), 1); }
    }

    internal BoardFillGoalBoard ReadBoard()
    {
        var goals = ReadGoals();
        return new(goals.Count(goal => !goal.IsTerminal), goals.Where(goal => goal.SourceBacklogItemId is not null)
            .Select(goal => goal.SourceBacklogItemId!).ToHashSet(StringComparer.Ordinal));
    }

    private IReadOnlyCollection<Goal> ReadGoals() => SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
        .LoadAsync().GetAwaiter().GetResult().Goals;

    private void Execute(IReadOnlyList<string> args)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
        var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
        Goal? currentGoal = null;
        _ = CliPersistentStateRunner.ExecuteCommand(args, new SqliteOrchestratorStateRepository(workspace.SqliteStatePath),
            workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
            NullOperatorChannel.Instance);
    }
}
