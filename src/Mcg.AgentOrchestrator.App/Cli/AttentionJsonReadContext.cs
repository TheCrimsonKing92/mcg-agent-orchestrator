using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

/// <summary>One query-only attention invocation, including the explicit legacy query capability.</summary>
internal sealed record AttentionJsonReadContext(
    IReadOnlyList<HumanInputRequest> Waits, IReadOnlyList<CollaborationItem> Items,
    IReadOnlyList<ProviderUsageHoldRow> ProviderHolds, IReadOnlyList<AttentionGateEvidence> GateEvidence,
    IReadOnlyList<OwnerQuestionHoldBlocker> OwnerQuestions)
{
    internal static async Task<AttentionJsonReadContext> ReadAsync(IOrchestratorStateQueries queries,
        OrchestratorWorkspace workspace, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projection = await queries.ReadAttentionProjectionAsync(cancellationToken);
        var itemsDb = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
        IReadOnlyList<CollaborationItem> items = File.Exists(itemsDb)
            ? await CollaborationItemStore.OpenExisting(workspace.OrchestratorDirectory)
                .GetAttentionQueueAsync(cancellationToken: cancellationToken) : [];

        if (projection is null)
        {
            // Unsupported is a declared capability value. Do not catch IO, corruption,
            // cancellation or arbitrary NotSupportedException from implemented operations.
            var metadata = await queries.ListGoalMetadataAsync(cancellationToken);
            var fallbackIds = metadata.Select(goal => new GoalId(goal.Id)).ToArray();
            var baseline = fallbackIds.Length == 0 ? new AgentOrchestratorKernel()
                : await queries.LoadGoalsAsync(fallbackIds, cancellationToken);
            return FromKernel(baseline, items, now);
        }

        var facts = projection.Goals.ToDictionary(goal => goal.GoalId, StringComparer.OrdinalIgnoreCase);
        var openWaits = await queries.ListOpenHumanInputRequestsAsync(cancellationToken);
        var currentWaits = openWaits.Where(wait => facts.TryGetValue(wait.GoalId, out var goal) &&
            (wait.PlannerIdentity is null || wait.PlannerIdentity.BriefVersion == goal.BriefVersion)).ToArray();
        // The shared selection's parked decline belongs to the text writer fallback. Excluding
        // parked metadata here applies JSON's existing live filter without ever declining.
        var metadataForSelection = projection.Goals.Where(goal => goal.Status != GoalStatus.Parked)
            .Select(goal => new GoalSummary(goal.GoalId, goal.Status.ToString(), "", "")).ToArray();
        var selection = AttentionReadGoalSelection.Select(metadataForSelection, currentWaits, [], items);
        // Kernel restore can repair a missing persisted request from a WaitingForHuman task.
        // Such owners are potential actionable evidence even with no request-table row.
        var ids = selection.GoalIds.Concat(projection.Goals.Where(goal => goal.HasWaitingTasks &&
                goal.Status is not (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or
                    GoalStatus.Cancelled or GoalStatus.Superseded))
            .Select(goal => new GoalId(goal.GoalId))).Distinct().OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();
        var kernel = ids.Length == 0 ? new AgentOrchestratorKernel()
            : await queries.LoadGoalsAsync(ids, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new AttentionJsonReadContext(CliCommandHandlers.HumanWaitsForAttention(kernel, null, includeHistory: false),
            CliCommandHandlers.CollaborationItemsForAttention(items, kernel, null, includeHistory: false),
            ProviderUsageHoldView.ReadEvidence(projection.ProviderEvidence, now, kernel.Goals), projection.GateEvidence,
            projection.OwnerQuestions.Select(ReadOwnerQuestion).ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static AttentionJsonReadContext FromKernel(AgentOrchestratorKernel kernel,
        IReadOnlyList<CollaborationItem> items, DateTimeOffset now) => new(
        CliCommandHandlers.HumanWaitsForAttention(kernel, null, includeHistory: false),
        CliCommandHandlers.CollaborationItemsForAttention(items, kernel, null, includeHistory: false),
        ProviderUsageHoldView.Read(kernel.Goals, now),
        kernel.Goals.SelectMany(goal => goal.GateTestObligations
            .Where(item => item.BriefVersion == goal.AuthoritativeBrief.Version)
            .Select(item => new AttentionGateEvidence(goal.Id.Value, item))).ToArray(),
        kernel.Goals.Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
            .Select(OwnerQuestionHoldBlocker.TryRead).OfType<OwnerQuestionHoldBlocker>().ToArray());

    private static OwnerQuestionHoldBlocker ReadOwnerQuestion(AttentionOwnerQuestionEvidence evidence)
    {
        // Only a displayed hold gets this sparse formatting adapter: ordered IDs and the hold,
        // with no historical task graphs, outputs, histories or timeline. Reuse the canonical
        // formatter, including malformed-marker fallback and original task-number semantics.
        var tasks = evidence.TaskIds.Select(id => new TaskSnapshot(id, "Owner question input",
            AgentRole.Developer, WorkTaskStatus.Pending, null, null, null, null, null, null)).ToArray();
        var adapter = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot(evidence.GoalId, "Owner question input", GoalStatus.Active, tasks, [],
                CurrentHold: evidence.Hold)], []));
        return OwnerQuestionHoldBlocker.TryRead(adapter.Goals.Single())!;
    }
}
