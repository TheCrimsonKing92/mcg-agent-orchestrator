using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public enum OperatorInboxSeverity
{
    Info,
    Warning,
    Blocker
}

public enum OperatorInboxKind
{
    HumanInput,
    FailedTask,
    FailedVerification,
    RunningWorker,
    MissingVerification,
    AcceptanceGate,
    SupervisorProposal,
    SubscriptionRouteWarning,
    ReadinessPreflight,
    BudgetWarning,
    LandingEscalation
}

public sealed record OperatorInboxReport(
    string? GoalPrefix,
    int TotalCount,
    int OpenCount,
    int AcknowledgedCount,
    IReadOnlyList<OperatorInboxItem> Items);

public sealed record OperatorInboxItem(
    string Id,
    OperatorInboxKind Kind,
    OperatorInboxSeverity Severity,
    string GoalId,
    string GoalPrefix,
    string Objective,
    string? TaskId,
    int? TaskNumber,
    string Title,
    string Message,
    string Evidence,
    string SuggestedAction,
    string SuggestedCommand,
    string Source,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgementNote);

internal static class OperatorInbox
{
    private const string StoreFileName = "operator-inbox-acks.json";
    private const string LandingEscalationFileName = "landing-escalations.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static OperatorInboxReport Build(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        OrchestratorWorkspace workspace,
        string? goalPrefix = null,
        bool includeAcknowledged = false)
    {
        var acknowledgements = LoadAcknowledgements(workspace).ToDictionary(item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        var goals = ResolveGoals(kernel, goalPrefix);
        var items = new Dictionary<string, OperatorInboxItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var goal in goals)
        {
            AddHumanInputItems(items, kernel, goal, acknowledgements);
            AddMonitorItems(items, kernel, goal, acknowledgements);
            AddReadinessItems(items, goal, agents, workspace, acknowledgements);
            AddAcceptanceItems(items, kernel, goal, acknowledgements);
            AddSupervisorItems(items, kernel, goal, agents, workspace, acknowledgements);
            AddSubscriptionRouteItems(items, goal, agents, workerProfiles, acknowledgements);
            AddBudgetItems(items, goal, agents, workerProfiles, acknowledgements);
            AddLandingEscalationItems(items, goal, workspace, acknowledgements);
        }

        var ordered = items.Values
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.GoalPrefix, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TaskNumber ?? int.MaxValue)
            .ThenBy(item => item.Kind)
            .ToList();
        var totalCount = ordered.Count;
        var visible = includeAcknowledged
            ? ordered
            : ordered.Where(item => !item.Acknowledged).ToList();

        return new OperatorInboxReport(
            string.IsNullOrWhiteSpace(goalPrefix) ? null : goalPrefix,
            totalCount,
            ordered.Count(item => !item.Acknowledged),
            ordered.Count(item => item.Acknowledged),
            visible);
    }

    // Lightweight acknowledgement without a full kernel load — used by the Discord gateway
    // listener where we want to ack the inbox item after dispatch without re-loading state.
    // If the itemId doesn't match a live inbox item, the record is still stored and silently
    // ignored when the inbox report is next built.
    public static void AppendAcknowledgement(OrchestratorWorkspace workspace, string itemId)
    {
        var acknowledgements = LoadAcknowledgements(workspace)
            .Where(item => !item.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
            .Append(new OperatorInboxAcknowledgement(itemId, DateTimeOffset.UtcNow, null))
            .OrderBy(item => item.AcknowledgedAt)
            .ToList();
        SaveAcknowledgements(workspace, acknowledgements);
    }

    public static OperatorInboxReport Acknowledge(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        OrchestratorWorkspace workspace,
        string itemId,
        string? note,
        string? goalPrefix = null)
    {
        var current = Build(kernel, agents, workerProfiles, workspace, goalPrefix, includeAcknowledged: true);
        if (current.Items.All(item => !item.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new KeyNotFoundException($"Operator inbox item '{itemId}' was not found.");
        }

        var acknowledgements = LoadAcknowledgements(workspace)
            .Where(item => !item.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
            .Append(new OperatorInboxAcknowledgement(itemId, DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(note) ? null : note))
            .OrderBy(item => item.AcknowledgedAt)
            .ToList();
        SaveAcknowledgements(workspace, acknowledgements);
        return Build(kernel, agents, workerProfiles, workspace, goalPrefix, includeAcknowledged: true);
    }

    private static void AddHumanInputItems(
        Dictionary<string, OperatorInboxItem> items,
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        foreach (var item in kernel.BuildHumanInputWorklist(goal.Id).Items)
        {
            var taskNumber = GetTaskNumber(goal, item.TaskId);
            Add(items, BuildItem(
                goal,
                OperatorInboxKind.HumanInput,
                OperatorInboxSeverity.Blocker,
                item.TaskId,
                taskNumber,
                $"Human input required for {WorkItemLabel(taskNumber)}",
                item.Question,
                item.SuggestedAction,
                item.SuggestedAction,
                $"answer {item.RequestId.Value[..8]} <answer>",
                $"human-input:{item.RequestId.Value}",
                acknowledgements));
        }
    }

    private static void AddMonitorItems(
        Dictionary<string, OperatorInboxItem> items,
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        foreach (var item in kernel.BuildMonitor(goal.Id).AttentionItems)
        {
            if (item.Kind == TaskAttentionKind.PendingHumanInput)
            {
                continue;
            }

            var taskNumber = GetTaskNumber(goal, item.TaskId);
            var (kind, severity, command) = item.Kind switch
            {
                TaskAttentionKind.FailedTask => (OperatorInboxKind.FailedTask, OperatorInboxSeverity.Blocker, $"next {goal.Id.Value[..8]}"),
                TaskAttentionKind.FailedVerification => (OperatorInboxKind.FailedVerification, OperatorInboxSeverity.Blocker, $"verify-needed {goal.Id.Value[..8]}"),
                TaskAttentionKind.RunningDispatch => (OperatorInboxKind.RunningWorker, OperatorInboxSeverity.Warning, taskNumber is null ? $"supervisor {goal.Id.Value[..8]}" : $"refresh-dispatch {goal.Id.Value[..8]} {taskNumber}"),
                TaskAttentionKind.MissingVerification => (OperatorInboxKind.MissingVerification, OperatorInboxSeverity.Blocker, $"verify-needed {goal.Id.Value[..8]}"),
                _ => (OperatorInboxKind.SupervisorProposal, OperatorInboxSeverity.Warning, $"supervisor {goal.Id.Value[..8]}")
            };

            Add(items, BuildItem(
                goal,
                kind,
                severity,
                item.TaskId,
                taskNumber,
                $"{Display(item.Kind)} on {WorkItemLabel(taskNumber)}",
                item.Message,
                item.Message,
                "Inspect the goal and apply the next safe action.",
                command,
                $"monitor:{item.Kind}:{item.TaskId?.Value ?? "goal"}:{item.Message}",
                acknowledgements));
        }
    }

    private static void AddAcceptanceItems(
        Dictionary<string, OperatorInboxItem> items,
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        if (goal.Status != GoalStatus.Completed)
        {
            return;
        }

        var summary = kernel.BuildGoalAcceptanceSummary(goal.Id);
        if (summary.IsAccepted)
        {
            Add(items, BuildItem(
                goal,
                OperatorInboxKind.AcceptanceGate,
                OperatorInboxSeverity.Blocker,
                null,
                null,
                "Goal is ready for acceptance",
                "All verification gates are satisfied; merge and cleanup remain operator-gated.",
                "Goal acceptance summary",
                "Run the acceptance command after reviewing the goal branch diff.",
                $"acceptance {goal.Id.Value[..8]} --autonomy supervised-auto",
                $"acceptance:ready:{goal.Id.Value}",
                acknowledgements));
            return;
        }

        foreach (var blocker in summary.Blockers)
        {
            var taskNumber = GetTaskNumber(goal, blocker.TaskId);
            Add(items, BuildItem(
                goal,
                OperatorInboxKind.AcceptanceGate,
                OperatorInboxSeverity.Blocker,
                blocker.TaskId,
                taskNumber,
                $"Acceptance blocked for {WorkItemLabel(taskNumber)}",
                blocker.Message,
                blocker.SuggestedAction,
                blocker.SuggestedAction,
                BuildAcceptanceBlockerCommand(goal, blocker, taskNumber),
                $"acceptance:blocker:{blocker.Kind}:{blocker.TaskId?.Value ?? blocker.HumanInputRequestId?.Value ?? "goal"}",
                acknowledgements));
        }
    }

    private static void AddReadinessItems(
        Dictionary<string, OperatorInboxItem> items,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        OrchestratorWorkspace workspace,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        if (goal.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded)
        {
            return;
        }

        var readiness = GoalReadinessPreflight.Build(goal, agents, workspace.ExecutionDirectory);
        foreach (var finding in readiness.Findings.Where(finding => finding.Severity != GoalReadinessSeverity.Info))
        {
            var severity = finding.Severity == GoalReadinessSeverity.Blocker
                ? OperatorInboxSeverity.Blocker
                : OperatorInboxSeverity.Warning;
            var command = finding.Kind == "workspace-missing"
                ? $"workspace create {goal.Id.Value[..8]}"
                : $"readiness {goal.Id.Value[..8]}";
            Add(items, BuildItem(
                goal,
                OperatorInboxKind.ReadinessPreflight,
                severity,
                null,
                null,
                $"Readiness {finding.Severity}: {finding.Kind}",
                finding.Message,
                $"recommendation={readiness.Recommendation}; override={finding.CanOverride}",
                finding.CanOverride
                    ? "Review readiness and confirm the risk before unattended start."
                    : "Resolve the readiness blocker before unattended start.",
                command,
                $"readiness:{finding.Kind}:{finding.Message}",
                acknowledgements));
        }
    }

    private static void AddSupervisorItems(
        Dictionary<string, OperatorInboxItem> items,
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        OrchestratorWorkspace workspace,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        var plan = GoalSupervisor.Build(kernel, goal, agents, workspace.ExecutionDirectory, AutonomyPolicy.SafeAuto);
        foreach (var proposal in plan.Proposals.Where(proposal => proposal.Kind != GoalSupervisorProposalKind.Monitor))
        {
            var severity = proposal.RequiresOperatorGate || !proposal.PolicyAllows
                ? OperatorInboxSeverity.Blocker
                : OperatorInboxSeverity.Warning;
            Add(items, BuildItem(
                goal,
                OperatorInboxKind.SupervisorProposal,
                severity,
                proposal.TaskId,
                proposal.TaskNumber,
                $"{proposal.Kind} proposal for {WorkItemLabel(proposal.TaskNumber)}",
                proposal.Reason,
                $"policy={plan.PolicyName}; canApply={proposal.CanApply}; gate={proposal.RequiresOperatorGate}",
                proposal.CanApply && !proposal.RequiresOperatorGate
                    ? "Supervisor can apply this reversible action under safe-auto."
                    : "Operator decision is required before automation can continue.",
                proposal.SuggestedCommand,
                $"supervisor:{proposal.Kind}:{proposal.TaskId?.Value ?? "goal"}",
                acknowledgements));
        }
    }

    private static void AddSubscriptionRouteItems(
        Dictionary<string, OperatorInboxItem> items,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        var plan = SubscriptionPlanBuilder.Build(goal, agents, workerProfiles);
        foreach (var item in plan.Items.Where(item =>
            item.TaskStatus == WorkTaskStatus.Assigned &&
            item.Route is not null &&
            item.Route.Disposition != WorkerRouteDisposition.Selected))
        {
            var severity = item.Route!.Disposition == WorkerRouteDisposition.Blocked
                ? OperatorInboxSeverity.Blocker
                : OperatorInboxSeverity.Warning;
            Add(items, BuildItem(
                goal,
                OperatorInboxKind.SubscriptionRouteWarning,
                severity,
                new TaskId(item.TaskId),
                item.TaskNumber,
                $"Subscription route {item.Route.Disposition} for task {item.TaskNumber}",
                item.Route.Recommendation,
                string.Join("; ", item.Route.Reasons),
                item.Detail,
                $"subscription-plan {goal.Id.Value[..8]}",
                $"subscription-route:{item.TaskId}:{item.Route.Disposition}",
                acknowledgements));
        }
    }

    private static void AddBudgetItems(
        Dictionary<string, OperatorInboxItem> items,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        if (goal.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded)
        {
            return;
        }

        var plan = SubscriptionPlanBuilder.Build(goal, agents, workerProfiles);
        if (!string.IsNullOrWhiteSpace(plan.ReadyStartCostRisk))
        {
            Add(items, BuildItem(
                goal,
                OperatorInboxKind.BudgetWarning,
                OperatorInboxSeverity.Warning,
                null,
                null,
                "Subscription start has prompt cost risk",
                plan.ReadyStartCostRisk,
                string.Join("; ", plan.ReadyStartCostRiskDetails),
                plan.ReadyStartCostRecommendation ?? "Inspect the subscription plan before starting subscription workers.",
                $"subscription-plan {goal.Id.Value[..8]}",
                $"budget:ready-start:{plan.ReadyStartPromptCharacterCount}",
                acknowledgements));
        }

        foreach (var budget in plan.ProviderBudgets.Where(budget =>
            budget.IsCoolingDown || budget.RecoverableLimitFailureCount > 0))
        {
            var message = budget.IsCoolingDown
                ? $"Provider {budget.ProviderName} is cooling down until {budget.RetryAfter:u}."
                : $"Provider {budget.ProviderName} has {budget.RecoverableLimitFailureCount} recoverable limit failure(s).";
            Add(items, BuildItem(
                goal,
                OperatorInboxKind.BudgetWarning,
                OperatorInboxSeverity.Warning,
                null,
                null,
                $"Provider budget warning: {budget.ProviderName}",
                message,
                budget.Detail,
                "Wait for retry-after or route work to another provider before starting more subscription workers.",
                $"subscription-plan {goal.Id.Value[..8]}",
                $"budget:provider:{budget.ProviderName}:{budget.RetryAfter?.ToUnixTimeSeconds() ?? budget.RecoverableLimitFailureCount}",
                acknowledgements));
        }
    }

    public static void RecordLandingEscalation(
        OrchestratorWorkspace workspace,
        Goal goal,
        string reason,
        string integrationBranch,
        IOperatorChannel? channel = null,
        ICollaborationItemStore? collaborationStore = null)
    {
        var existing = LoadLandingEscalations(workspace)
            .Where(e => !e.GoalId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        existing.Add(new LandingEscalationRecord(goal.Id.Value, reason, integrationBranch, DateTimeOffset.UtcNow));
        SaveLandingEscalations(workspace, existing);

        var goalPrefix = goal.Id.Value[..8];
        var sourceKey = $"landing-escalation:{goal.Id.Value}:{reason}";
        var itemId = BuildId(goal.Id, OperatorInboxKind.LandingEscalation, sourceKey);

        var store = collaborationStore
            ?? CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        try
        {
            store.RaiseAsync(
                CollaborationItemType.Decision,
                goal.Id.Value,
                $"Landing parked on {integrationBranch}",
                reason,
                itemId).GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort: JSON fallback already written.
        }

        if (channel is null or NullOperatorChannel)
            return;

        var escalation = new OperatorEscalation(
            itemId,
            goal.Id.Value,
            goalPrefix,
            "LandingEscalation",
            $"Landing parked on {integrationBranch}",
            reason,
            $"escalated at {DateTimeOffset.UtcNow:u}; branch={integrationBranch}",
            [new OperatorEscalationAction("Promote to Main", $"land {goalPrefix}", RequiresConfirm: true)],
            null);
        try
        {
            channel.SendEscalationAsync(escalation).GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort: inbox JSON write already succeeded.
        }
    }

    private static void AddLandingEscalationItems(
        Dictionary<string, OperatorInboxItem> items,
        Goal goal,
        OrchestratorWorkspace workspace,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        var escalations = LoadLandingEscalations(workspace);
        foreach (var escalation in escalations.Where(e =>
            e.GoalId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase)))
        {
            Add(items, BuildItem(
                goal,
                OperatorInboxKind.LandingEscalation,
                OperatorInboxSeverity.Warning,
                null,
                null,
                $"Landing parked on {escalation.IntegrationBranch}",
                escalation.Reason,
                $"escalated at {escalation.EscalatedAt:u}; branch={escalation.IntegrationBranch}",
                "Review the change and promote manually or rerun land after resolving the issue.",
                $"land {goal.Id.Value[..8]}",
                $"landing-escalation:{escalation.GoalId}:{escalation.Reason}",
                acknowledgements));
        }
    }

    private static IReadOnlyList<LandingEscalationRecord> LoadLandingEscalations(OrchestratorWorkspace workspace)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, LandingEscalationFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<LandingEscalationStore>(File.ReadAllText(path), JsonOptions)?.Items ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void SaveLandingEscalations(OrchestratorWorkspace workspace, IReadOnlyList<LandingEscalationRecord> items)
    {
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        File.WriteAllText(
            Path.Combine(workspace.OrchestratorDirectory, LandingEscalationFileName),
            JsonSerializer.Serialize(new LandingEscalationStore(items), JsonOptions));
    }

    private static OperatorInboxItem BuildItem(
        Goal goal,
        OperatorInboxKind kind,
        OperatorInboxSeverity severity,
        TaskId? taskId,
        int? taskNumber,
        string title,
        string message,
        string evidence,
        string suggestedAction,
        string suggestedCommand,
        string sourceKey,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        var id = BuildId(goal.Id, kind, sourceKey);
        acknowledgements.TryGetValue(id, out var acknowledgement);
        return new OperatorInboxItem(
            id,
            kind,
            severity,
            goal.Id.Value,
            goal.Id.Value[..8],
            goal.Objective,
            taskId?.Value,
            taskNumber,
            title,
            message,
            evidence,
            suggestedAction,
            suggestedCommand,
            sourceKey,
            acknowledgement is not null,
            acknowledgement?.AcknowledgedAt,
            acknowledgement?.Note);
    }

    private static void Add(Dictionary<string, OperatorInboxItem> items, OperatorInboxItem item)
    {
        items.TryAdd(item.Id, item);
    }

    private static List<Goal> ResolveGoals(AgentOrchestratorKernel kernel, string? goalPrefix)
    {
        if (!string.IsNullOrWhiteSpace(goalPrefix))
        {
            return [OrchestratorEntityResolver.ResolveGoal(kernel, null, goalPrefix)];
        }

        return kernel.Goals
            .Where(goal => goal.Status is not GoalStatus.Cancelled and not GoalStatus.Superseded)
            .OrderByDescending(goal => goal.Timeline.LastOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private static int? GetTaskNumber(Goal goal, TaskId? taskId)
    {
        if (taskId is null)
        {
            return null;
        }

        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            if (goal.Tasks[index].Id == taskId)
            {
                return index + 1;
            }
        }

        return null;
    }

    private static string BuildAcceptanceBlockerCommand(Goal goal, GoalAcceptanceBlocker blocker, int? taskNumber)
    {
        if (blocker.HumanInputRequestId is not null)
        {
            return $"answer {blocker.HumanInputRequestId.Value[..8]} <answer>";
        }

        return taskNumber is null
            ? $"acceptance {goal.Id.Value[..8]}"
            : $"verify {taskNumber} <command>";
    }

    private static string BuildId(GoalId goalId, OperatorInboxKind kind, string sourceKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{goalId.Value}|{kind}|{sourceKey}"));
        return $"inbox-{Convert.ToHexString(bytes)[..12].ToLowerInvariant()}";
    }

    private static string WorkItemLabel(int? taskNumber) => taskNumber is null ? "goal" : $"task {taskNumber}";

    private static string Display(TaskAttentionKind kind) => kind switch
    {
        // inbox-specific label for running dispatches
        TaskAttentionKind.RunningDispatch => "Running worker",
        _ => DashboardDisplayNames.Display(kind)
    };

    private static IReadOnlyList<OperatorInboxAcknowledgement> LoadAcknowledgements(OrchestratorWorkspace workspace)
    {
        var path = StorePath(workspace);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<OperatorInboxAcknowledgementStore>(File.ReadAllText(path), JsonOptions)?.Items ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void SaveAcknowledgements(OrchestratorWorkspace workspace, IReadOnlyList<OperatorInboxAcknowledgement> items)
    {
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        File.WriteAllText(StorePath(workspace), JsonSerializer.Serialize(new OperatorInboxAcknowledgementStore(items), JsonOptions));
    }

    private static string StorePath(OrchestratorWorkspace workspace) => Path.Combine(workspace.OrchestratorDirectory, StoreFileName);

    private sealed record OperatorInboxAcknowledgement(string ItemId, DateTimeOffset AcknowledgedAt, string? Note);

    private sealed record OperatorInboxAcknowledgementStore(IReadOnlyList<OperatorInboxAcknowledgement> Items);

    private sealed record LandingEscalationRecord(
        string GoalId,
        string Reason,
        string IntegrationBranch,
        DateTimeOffset EscalatedAt);

    private sealed record LandingEscalationStore(IReadOnlyList<LandingEscalationRecord> Items);
}
