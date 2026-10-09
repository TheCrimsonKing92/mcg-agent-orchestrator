using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal enum EpicPlanSliceStatus { Blocked, Ready, InFlight, Verified, Landed, Failed, Parked, Done, Superseded, Missing }

internal sealed record EpicPlanItemStatus(EpicPlanItem Item, EpicPlanSliceStatus? Status, string Subject, string? Detail = null)
{
    internal string Render() => Status is null ? (Item.Done ? "done" : "open") :
        (Status == EpicPlanSliceStatus.InFlight ? "In flight" : Status.ToString())
        + (string.IsNullOrWhiteSpace(Detail) ? string.Empty : $" ({Detail})");
}

internal sealed record EpicPlanView(EpicPlan Plan, IReadOnlyList<EpicPlanItemStatus> Items, IReadOnlyList<PortfolioEpicMember> UnplannedMembers);

// Slice statuses are projections of backlog/goal state, never values written to portfolio.db.
internal static class EpicPlanStatusReader
{
    internal static IReadOnlyDictionary<string, EpicPlanView> Load(OrchestratorWorkspace workspace,
        IReadOnlyList<PortfolioEpic> epics, bool includeReasons = false)
    {
        if (epics.Count == 0) return new Dictionary<string, EpicPlanView>();
        var planStore = EpicPlanStore.OpenReadOnly(workspace.PortfolioStorePath);
        var plans = epics.Select(epic => planStore.LoadAsync(epic.Id).GetAwaiter().GetResult()).ToArray();
        var portfolio = PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath);
        var backlog = BoardFillBacklogSnapshot.Read(workspace.BacklogStorePath).ToDictionary(item => item.Id, StringComparer.Ordinal);
        var goals = EpicProgressReadModel.LoadGoalMetadata(workspace).ToDictionary(goal => goal.Id, StringComparer.Ordinal);
        var claims = EpicPlanGoalClaimSnapshot.Read(workspace.SqliteStatePath);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        if (includeReasons && File.Exists(workspace.SqliteStatePath))
        {
            var plannedIds = plans.SelectMany(plan => plan.Items).Where(item => item.BacklogItemId is not null)
                .Select(item => claims.ByBacklog.GetValueOrDefault(item.BacklogItemId!)).Where(id => id is not null)
                .Select(id => id!).Distinct(StringComparer.Ordinal)
                .Where(id => goals.TryGetValue(id, out var goal) && goal.Status is "Failed" or "AcceptanceFailed" or "Parked").ToArray();
            if (plannedIds.Length > 0)
            {
                var kernel = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                    .LoadGoalsAsync(plannedIds.Select(id => new GoalId(id)).ToArray()).GetAwaiter().GetResult();
                foreach (var goal in kernel.Goals)
                {
                    var events = goal.Timeline.OrderByDescending(item => item.OccurredAt).ToArray();
                    var reason = goal.Status == GoalStatus.Parked
                        ? events.FirstOrDefault(item => item.Kind == ProgressKind.GoalPolicyDecision &&
                            item.Message.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase))?.Message
                        : events.FirstOrDefault(item => item.Kind == ProgressKind.TaskFailed)?.Message;
                    if (string.IsNullOrWhiteSpace(reason))
                        reason = events.FirstOrDefault(item => item.Kind == ProgressKind.GoalPolicyDecision)?.Message;
                    if (reason is not null) reasons[goal.Id.Value] = reason;
                }
            }
        }
        return plans.ToDictionary(plan => plan.EpicId, plan =>
        {
            var plannedBacklog = plan.Items.Where(item => item.Kind == EpicPlanItemKind.Slice)
                .Select(item => item.BacklogItemId!).ToHashSet(StringComparer.Ordinal);
            var unplanned = portfolio.ListEpicMembersAsync(plan.EpicId).GetAwaiter().GetResult().Where(member =>
                member.Kind == PortfolioMemberKind.BacklogItem ? !plannedBacklog.Contains(member.MemberId) :
                !claims.ByGoal.TryGetValue(member.MemberId, out var id) || !plannedBacklog.Contains(id)).ToArray();
            return new EpicPlanView(plan, Compute(plan, backlog, goals, claims.ByBacklog, reasons), unplanned);
        }, StringComparer.Ordinal);
    }

    internal static IReadOnlyList<EpicPlanItemStatus> Compute(EpicPlan plan,
        IReadOnlyDictionary<string, BacklogItem> backlog, IReadOnlyDictionary<string, GoalSummary> goals,
        IReadOnlyDictionary<string, string> claims, IReadOnlyDictionary<string, string>? reasons = null) =>
        plan.Items.OrderBy(item => item.Position).Select(item => ComputeItem(item, backlog, goals, claims, reasons)).ToArray();

    private static EpicPlanItemStatus ComputeItem(EpicPlanItem item,
        IReadOnlyDictionary<string, BacklogItem> backlog, IReadOnlyDictionary<string, GoalSummary> goals,
        IReadOnlyDictionary<string, string> claims, IReadOnlyDictionary<string, string>? reasons)
    {
        if (item.Kind == EpicPlanItemKind.Step) return new(item, null, item.Text!);
        var id = item.BacklogItemId!;
        if (!backlog.TryGetValue(id, out var source)) return new(item, EpicPlanSliceStatus.Missing, Short(id));
        var subject = $"{Short(id)} {source.Title}";
        if (source.Status == BacklogItemStatus.Superseded) return new(item, EpicPlanSliceStatus.Superseded, subject);
        if (claims.TryGetValue(id, out var goalId) && goals.TryGetValue(goalId, out var goal))
        {
            if (!Enum.TryParse<GoalStatus>(goal.Status, out var state))
                throw new InvalidOperationException($"Unknown goal status '{goal.Status}' for {goalId}.");
            var bucket = EpicProgressReadModel.BucketOf(state);
            switch (bucket)
            {
                case EpicProgressBucket.Active:
                case EpicProgressBucket.Verifying:
                    return new(item, EpicPlanSliceStatus.InFlight, subject,
                        $"{Short(goalId)} {goal.Status}" + (string.IsNullOrWhiteSpace(goal.Condition) ? "" : $" {goal.Condition}"));
                case EpicProgressBucket.Verified: return new(item, EpicPlanSliceStatus.Verified, subject);
                case EpicProgressBucket.Landed: return new(item, EpicPlanSliceStatus.Landed, subject);
                case EpicProgressBucket.Failed: return new(item, EpicPlanSliceStatus.Failed, subject, reasons?.GetValueOrDefault(goalId));
                case EpicProgressBucket.Parked: return new(item, EpicPlanSliceStatus.Parked, subject, reasons?.GetValueOrDefault(goalId));
                // Cancelled/superseded goals do not supersede their source backlog item.
                case EpicProgressBucket.Closed: break;
                default: throw new InvalidOperationException($"Unexpected goal bucket '{bucket}' for {goalId}.");
            }
        }
        if (source.Status == BacklogItemStatus.Done) return new(item, EpicPlanSliceStatus.Done, subject);
        foreach (var dependency in source.Dependencies)
        {
            var open = dependency.TargetKind == BacklogDependencyTargetKind.Backlog
                ? !backlog.TryGetValue(dependency.PrerequisiteId, out var prerequisite) || prerequisite.Status == BacklogItemStatus.Open
                : !goals.TryGetValue(dependency.PrerequisiteId, out var prerequisiteGoal) || prerequisiteGoal.Status != nameof(GoalStatus.Completed);
            if (open) return new(item, EpicPlanSliceStatus.Blocked, subject, $"blocked on {dependency.PrerequisiteId}");
        }
        return new(item, EpicPlanSliceStatus.Ready, subject);
    }

    private static string Short(string id) => id[..Math.Min(8, id.Length)];
}
