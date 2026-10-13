using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal enum EpicPlanSliceStatus { Blocked, Ready, InFlight, Verified, Landed, Failed, Parked, Done, Superseded, Missing, RemainingScope, ReconciliationRequired, Unresolved, Claimed }

internal sealed record EpicPlanItemStatus(EpicPlanItem Item, EpicPlanSliceStatus? Status, string Subject, string? Detail = null,
    EpicPlanSourceFacts? SourceFacts = null)
{
    internal bool ScopeComplete => SourceFacts?.ScopeComplete ??
        Status is EpicPlanSliceStatus.Done or EpicPlanSliceStatus.Superseded;
    internal string Render() => Status is null ? (Item.Done ? "done" : "open") :
        (Status switch
        {
            EpicPlanSliceStatus.InFlight => "In flight",
            EpicPlanSliceStatus.RemainingScope => "Open: remaining scope",
            EpicPlanSliceStatus.ReconciliationRequired => "Open: reconciliation required",
            EpicPlanSliceStatus.Unresolved => "Open: unresolved ownership",
            EpicPlanSliceStatus.Claimed => "Open: claimed",
            _ => Status.ToString()
        })
        + (string.IsNullOrWhiteSpace(Detail) ? string.Empty : $" ({Detail})");
}

internal sealed record EpicPlanView(EpicPlan Plan, IReadOnlyList<EpicPlanItemStatus> Items, IReadOnlyList<PortfolioEpicMember> UnplannedMembers,
    EpicOutcomeProjection? Outcome = null, IReadOnlyList<PortfolioEpicMember>? UnplannedActiveMembers = null)
{
    internal EpicOutcomeProjection OutcomeProgress => Outcome ?? EpicOutcomeProjection.Unevaluated;
}

// Slice statuses are projections of backlog/goal state, never values written to portfolio.db.
internal static class EpicPlanStatusReader
{
    internal static IReadOnlyDictionary<string, EpicPlanView> Load(OrchestratorWorkspace workspace,
        IReadOnlyList<PortfolioEpic> epics, bool includeReasons = false,
        IReadOnlyList<BacklogItem>? backlogSnapshot = null, IReadOnlyList<GoalSummary>? goalSnapshot = null,
        IReadOnlyList<PortfolioEpicMember>? memberSnapshot = null)
    {
        if (epics.Count == 0) return new Dictionary<string, EpicPlanView>();
        var planStore = EpicPlanStore.OpenReadOnly(workspace.PortfolioStorePath);
        var plans = epics.Select(epic => planStore.LoadAsync(epic.Id).GetAwaiter().GetResult()).ToArray();
        var portfolio = PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath);
        var backlog = (backlogSnapshot ?? BoardFillBacklogSnapshot.Read(workspace.BacklogStorePath)).ToDictionary(item => item.Id, StringComparer.Ordinal);
        var goals = (goalSnapshot ?? EpicProgressReadModel.LoadGoalMetadata(workspace)).ToDictionary(goal => goal.Id, StringComparer.Ordinal);
        var claims = EpicPlanGoalClaimSnapshot.Read(workspace.SqliteStatePath);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        if (includeReasons && File.Exists(workspace.SqliteStatePath))
        {
            var plannedIds = plans.SelectMany(plan => plan.Items).Where(item => item.BacklogItemId is not null)
                .Where(item => backlog.ContainsKey(item.BacklogItemId!))
                .Select(item => claims.Resolve(backlog[item.BacklogItemId!]).OwnerGoalId).Where(id => id is not null)
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
            var members = memberSnapshot?.Where(member => member.EpicId == plan.EpicId).ToArray() ??
                portfolio.ListEpicMembersAsync(plan.EpicId).GetAwaiter().GetResult();
            var unplanned = members.Where(member =>
                member.Kind == PortfolioMemberKind.BacklogItem ? !plannedBacklog.Contains(member.MemberId) :
                !(claims.ByGoal.TryGetValue(member.MemberId, out var id) && plannedBacklog.Contains(id)) &&
                !claims.Persisted.Any(pair => pair.Value.OwnerGoalId == member.MemberId && plannedBacklog.Contains(pair.Key))).ToArray();
            return new EpicPlanView(plan, Compute(plan, backlog, goals, claims, reasons), unplanned,
                EpicOutcomeEvaluator.Evaluate(plan.Outcome, EpicOutcomeReceipts.Problem),
                unplanned.Where(member => member.Kind == PortfolioMemberKind.Goal
                    ? !goals.TryGetValue(member.MemberId, out var goal) || goal.Status is not ("Completed" or "Cancelled" or "Superseded")
                    : !backlog.TryGetValue(member.MemberId, out var item) || item.Status is not (BacklogItemStatus.Done or BacklogItemStatus.Superseded)).ToArray());
        }, StringComparer.Ordinal);
    }

    internal static IReadOnlyList<EpicPlanItemStatus> Compute(EpicPlan plan,
        IReadOnlyDictionary<string, BacklogItem> backlog, IReadOnlyDictionary<string, GoalSummary> goals,
        EpicPlanGoalClaims claims, IReadOnlyDictionary<string, string>? reasons = null) =>
        plan.Items.OrderBy(item => item.Position).Select(item => ComputeItem(item, backlog, goals, claims, reasons)).ToArray();

    private static EpicPlanItemStatus ComputeItem(EpicPlanItem item,
        IReadOnlyDictionary<string, BacklogItem> backlog, IReadOnlyDictionary<string, GoalSummary> goals,
        EpicPlanGoalClaims claims, IReadOnlyDictionary<string, string>? reasons)
    {
        if (item.Kind == EpicPlanItemKind.Step) return new(item, null, item.Text!);
        var id = item.BacklogItemId!;
        if (!backlog.TryGetValue(id, out var source)) return new(item, EpicPlanSliceStatus.Missing, Short(id));
        var subject = $"{Short(id)} {source.Title}";
        var facts = claims.Resolve(source);
        EpicPlanItemStatus Result(EpicPlanSliceStatus status, string? detail = null) => new(item, status, subject, detail, facts);
        if (source.Status == BacklogItemStatus.Superseded) return Result(EpicPlanSliceStatus.Superseded);
        if (source.Status == BacklogItemStatus.Done) return Result(EpicPlanSliceStatus.Done);
        if (facts.Resolution is EpicPlanOwnershipResolution.Ambiguous or EpicPlanOwnershipResolution.MissingOwner or EpicPlanOwnershipResolution.InvalidCoverage)
            return Result(EpicPlanSliceStatus.Unresolved, facts.Resolution.ToString());
        if (facts.OwnerGoalId is { } goalId && goals.TryGetValue(goalId, out var goal))
        {
            if (!Enum.TryParse<GoalStatus>(goal.Status, out var state))
                throw new InvalidOperationException($"Unknown goal status '{goal.Status}' for {goalId}.");
            var bucket = EpicProgressReadModel.BucketOf(state);
            switch (bucket)
            {
                case EpicProgressBucket.Active:
                case EpicProgressBucket.Verifying:
                    return Result(EpicPlanSliceStatus.InFlight,
                        $"{Short(goalId)} {goal.Status}" + (string.IsNullOrWhiteSpace(goal.Condition) ? "" : $" {goal.Condition}"));
                case EpicProgressBucket.Verified: return Result(EpicPlanSliceStatus.Verified);
                case EpicProgressBucket.Landed: return Result(facts.Coverage == SourceBacklogCoverage.Slice
                    ? EpicPlanSliceStatus.RemainingScope : EpicPlanSliceStatus.ReconciliationRequired);
                case EpicProgressBucket.Failed: return Result(EpicPlanSliceStatus.Failed, reasons?.GetValueOrDefault(goalId));
                case EpicProgressBucket.Parked: return Result(EpicPlanSliceStatus.Parked, reasons?.GetValueOrDefault(goalId));
                // Cancelled/superseded goals do not supersede their source backlog item.
                case EpicProgressBucket.Closed: return Result(EpicPlanSliceStatus.Claimed);
                default: throw new InvalidOperationException($"Unexpected goal bucket '{bucket}' for {goalId}.");
            }
        }
        foreach (var dependency in source.Dependencies)
        {
            var open = dependency.TargetKind == BacklogDependencyTargetKind.Backlog
                ? !backlog.TryGetValue(dependency.PrerequisiteId, out var prerequisite) || prerequisite.Status == BacklogItemStatus.Open
                : !goals.TryGetValue(dependency.PrerequisiteId, out var prerequisiteGoal) || prerequisiteGoal.Status != nameof(GoalStatus.Completed);
            if (open) return Result(EpicPlanSliceStatus.Blocked, $"blocked on {dependency.PrerequisiteId}");
        }
        return Result(EpicPlanSliceStatus.Ready);
    }

    private static string Short(string id) => id[..Math.Min(8, id.Length)];
}
