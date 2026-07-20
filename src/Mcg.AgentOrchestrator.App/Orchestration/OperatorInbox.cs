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
    LandingEscalation,
    OwnershipHold,
    HoldPersistenceFailure,
    HoldClearanceFailure
}

public sealed record OwnershipHoldRequest(
    TaskId TaskId,
    int TaskNumber,
    AgentRole Role,
    IReadOnlyList<string> ApprovalPaths,
    string Reason);

public sealed record OwnershipHoldClearanceReceipt(
    string GoalId,
    IReadOnlyList<string> ClearedHoldIds,
    string TriggeringCommand,
    DateTimeOffset ClearedAt);

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
    DateTimeOffset RaisedAt,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgementNote);

internal static class OperatorInbox
{
    private const string StoreFileName = "operator-inbox-acks.json";
    private const string LandingEscalationFileName = "landing-escalations.json";
    private const string OwnershipHoldFileName = "ownership-holds.json";
    private const string HoldFailureFileName = "ownership-hold-failures.json";
    internal const int MaxHoldWriteRetries = 5;
    private static readonly TimeSpan InitialHoldWriteBackoff = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxHoldWriteBackoff = TimeSpan.FromSeconds(2);
    internal static Action<TimeSpan> HoldWriteBackoff { get; set; } = Thread.Sleep;
    internal static Action<string, string> HoldStoreWriteAllText { get; set; } = File.WriteAllText;
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
            AddReadinessItems(items, goal, agents, workerProfiles, workspace, acknowledgements);
            AddAcceptanceItems(items, kernel, goal, workspace, acknowledgements);
            AddSupervisorItems(items, kernel, goal, agents, workspace, acknowledgements);
            AddSubscriptionRouteItems(items, goal, agents, workerProfiles, acknowledgements);
            AddBudgetItems(items, goal, agents, workerProfiles, acknowledgements);
            AddLandingEscalationItems(items, goal, workspace, acknowledgements);
            AddOwnershipHoldItems(items, goal, workspace, acknowledgements);
            AddHoldFailureItems(items, goal, workspace, acknowledgements);
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
                item.RequestedAt,
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
                ResolveRaisedAt(goal, item.TaskId),
                acknowledgements));
        }
    }

    private static void AddAcceptanceItems(
        Dictionary<string, OperatorInboxItem> items,
        AgentOrchestratorKernel kernel,
        Goal goal,
        OrchestratorWorkspace workspace,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        if (goal.Status != GoalStatus.Verified)
        {
            return;
        }

        var summary = GoalAcceptanceStatusProjector.Build(kernel, goal, workspace.ExecutionDirectory);
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
                summary.Outcomes.OrderByDescending(outcome => outcome.OccurredAt).Select(outcome => (DateTimeOffset?)outcome.OccurredAt).FirstOrDefault() ?? ResolveRaisedAt(goal, null),
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
                goal.LatestAcceptanceFailure?.OccurredAt ?? ResolveRaisedAt(goal, blocker.TaskId),
                acknowledgements));
        }
    }

    private static void AddReadinessItems(
        Dictionary<string, OperatorInboxItem> items,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        OrchestratorWorkspace workspace,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        if (goal.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded)
        {
            return;
        }

        var readiness = GoalReadinessPreflight.Build(goal, agents, workspace.ExecutionDirectory, workerProfiles);
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
                ResolveRaisedAt(goal, null),
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
                ResolveRaisedAt(goal, proposal.TaskId),
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
                ResolveRaisedAt(goal, new TaskId(item.TaskId)),
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
                ResolveRaisedAt(goal, null),
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
                budget.RetryAfter ?? ResolveRaisedAt(goal, null),
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

        var command = BuildEscalationCommand(goalPrefix, integrationBranch);
        var actionLabel = integrationBranch.StartsWith("conductor:", StringComparison.OrdinalIgnoreCase)
            ? "Resolve Escalation"
            : "Promote to Main";
        var requiresConfirm = command.StartsWith("land ", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("acceptance ", StringComparison.OrdinalIgnoreCase);
        var escalation = new OperatorEscalation(
            itemId,
            goal.Id.Value,
            goalPrefix,
            "LandingEscalation",
            $"Landing parked on {integrationBranch}",
            OperatorEscalationProjection.FormatSummary(
                goal.Objective,
                reason,
                BuildEscalationSuggestedAction(integrationBranch)),
            $"escalated at {DateTimeOffset.UtcNow:u}; branch={integrationBranch}",
            [new OperatorEscalationAction(actionLabel, command, RequiresConfirm: requiresConfirm, RequiresInput: command.Contains('<'))],
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

    public static IReadOnlyList<OperatorInboxItem> RecordOwnershipHolds(
        OrchestratorWorkspace workspace,
        Goal goal,
        IReadOnlyList<OwnershipHoldRequest> holdRequests,
        IOperatorChannel? channel = null)
    {
        if (holdRequests.Count == 0)
        {
            return [];
        }

        try
        {
            var records = LoadOwnershipHolds(workspace).ToList();
            var now = DateTimeOffset.UtcNow;
            var raised = new List<OperatorInboxItem>();
            foreach (var request in holdRequests)
            {
                var key = OwnershipHoldKey(goal.Id.Value, request.TaskId.Value);
                var existingIndex = records.FindIndex(record =>
                    record.Kind == OperatorInboxKind.OwnershipHold &&
                    record.ResolvedAt is null &&
                    record.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                var message = $"Task {request.TaskNumber} touched operator-approval path(s): {string.Join(", ", request.ApprovalPaths)}.";
                var evidence = $"role={request.Role}; paths={string.Join(", ", request.ApprovalPaths)}; reason={request.Reason}";
                OwnershipHoldRecord record;
                if (existingIndex >= 0)
                {
                    record = records[existingIndex] with
                    {
                        Message = message,
                        Evidence = evidence,
                        ApprovalPaths = request.ApprovalPaths,
                        RaisedAt = now
                    };
                    records[existingIndex] = record;
                }
                else
                {
                    record = new OwnershipHoldRecord(
                        BuildStoredId(goal.Id, OperatorInboxKind.OwnershipHold, key, now),
                        OperatorInboxKind.OwnershipHold,
                        key,
                        goal.Id.Value,
                        request.TaskId.Value,
                        request.TaskNumber,
                        request.Role,
                        request.ApprovalPaths,
                        $"Ownership approval required for task {request.TaskNumber}",
                        message,
                        evidence,
                        now,
                        null,
                        null);
                    records.Add(record);
                }

                var acknowledgements = LoadAcknowledgements(workspace)
                    .ToDictionary(item => item.ItemId, StringComparer.OrdinalIgnoreCase);
                raised.Add(BuildOwnershipHoldItem(goal, record, acknowledgements));
            }

            SaveOwnershipHoldsWithRetry(workspace, records);
            SendEscalations(channel, raised);
            return raised;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            RecordHoldPersistenceFailure(workspace, goal, holdRequests, ex);
            return [];
        }
    }

    public static OwnershipHoldClearanceReceipt ClearOwnershipHoldsAfterLanding(
        OrchestratorWorkspace workspace,
        Goal goal,
        string triggeringCommand)
    {
        var records = LoadOwnershipHolds(workspace).ToList();
        var now = DateTimeOffset.UtcNow;
        var open = records
            .Where(record =>
                record.Kind == OperatorInboxKind.OwnershipHold &&
                record.ResolvedAt is null &&
                record.GoalId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (open.Length == 0)
        {
            return new OwnershipHoldClearanceReceipt(goal.Id.Value, [], triggeringCommand, now);
        }

        var openIds = open.Select(record => record.Id).ToArray();
        for (var index = 0; index < records.Count; index++)
        {
            if (openIds.Contains(records[index].Id, StringComparer.OrdinalIgnoreCase))
            {
                records[index] = records[index] with
                {
                    ResolvedAt = now,
                    ClearanceTriggeringCommand = triggeringCommand
                };
            }
        }

        try
        {
            SaveOwnershipHoldsWithRetry(workspace, records);
            var receipt = new OwnershipHoldClearanceReceipt(goal.Id.Value, openIds, triggeringCommand, now);
            AppendConductEvent(workspace, "ownership-hold-cleared", goal.Id.Value[..8],
                $"OWNERSHIP_HOLD_CLEARED goal={goal.Id.Value[..8]} holds={string.Join(",", openIds)} command=\"{triggeringCommand}\"");
            return receipt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            foreach (var record in open)
            {
                RecordHoldClearanceFailure(workspace, goal, record, triggeringCommand, ex);
            }

            return new OwnershipHoldClearanceReceipt(goal.Id.Value, [], triggeringCommand, now);
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
                escalation.IntegrationBranch.StartsWith("conductor:", StringComparison.OrdinalIgnoreCase)
                    ? OperatorInboxSeverity.Blocker
                    : OperatorInboxSeverity.Warning,
                null,
                null,
                $"Landing parked on {escalation.IntegrationBranch}",
                $"Reason: {escalation.Reason}",
                $"escalated at {escalation.EscalatedAt:u}; branch={escalation.IntegrationBranch}",
                BuildEscalationSuggestedAction(escalation.IntegrationBranch),
                BuildEscalationCommand(goal.Id.Value[..8], escalation.IntegrationBranch),
                $"landing-escalation:{escalation.GoalId}:{escalation.Reason}",
                escalation.EscalatedAt,
                acknowledgements));
        }
    }

    private static void AddOwnershipHoldItems(
        Dictionary<string, OperatorInboxItem> items,
        Goal goal,
        OrchestratorWorkspace workspace,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        foreach (var record in LoadOwnershipHolds(workspace).Where(record =>
            record.ResolvedAt is null &&
            record.GoalId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase)))
        {
            Add(items, BuildOwnershipHoldItem(goal, record, acknowledgements));
        }
    }

    private static void AddHoldFailureItems(
        Dictionary<string, OperatorInboxItem> items,
        Goal goal,
        OrchestratorWorkspace workspace,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        foreach (var record in LoadHoldFailures(workspace).Where(record =>
            record.ResolvedAt is null &&
            record.GoalId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase)))
        {
            Add(items, BuildFailureItem(goal, record, acknowledgements));
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

    private static OperatorInboxItem BuildOwnershipHoldItem(
        Goal goal,
        OwnershipHoldRecord record,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        acknowledgements.TryGetValue(record.Id, out var acknowledgement);
        return new OperatorInboxItem(
            record.Id,
            record.Kind,
            OperatorInboxSeverity.Blocker,
            record.GoalId,
            goal.Id.Value[..8],
            goal.Objective,
            record.TaskId,
            record.TaskNumber,
            record.Title,
            record.Message,
            record.Evidence,
            "Review the operator-owned diff and run supervised acceptance to resolve this hold.",
            $"acceptance {goal.Id.Value[..8]} --autonomy supervised-auto",
            record.Key,
            record.RaisedAt,
            acknowledgement is not null,
            acknowledgement?.AcknowledgedAt,
            acknowledgement?.Note);
    }

    private static OperatorInboxItem BuildFailureItem(
        Goal goal,
        HoldFailureRecord record,
        IReadOnlyDictionary<string, OperatorInboxAcknowledgement> acknowledgements)
    {
        acknowledgements.TryGetValue(record.Id, out var acknowledgement);
        return new OperatorInboxItem(
            record.Id,
            record.Kind,
            OperatorInboxSeverity.Blocker,
            record.GoalId,
            goal.Id.Value[..8],
            goal.Objective,
            record.TaskId,
            record.TaskNumber,
            record.Title,
            record.Message,
            record.Evidence,
            "Resolve the hold bookkeeping failure, then recover the goal with an operator note.",
            $"recover {goal.Id.Value[..8]} <note>",
            record.Key,
            record.RaisedAt,
            acknowledgement is not null,
            acknowledgement?.AcknowledgedAt,
            acknowledgement?.Note);
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
        DateTimeOffset raisedAt,
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
            raisedAt,
            acknowledgement is not null,
            acknowledgement?.AcknowledgedAt,
            acknowledgement?.Note);
    }

    private static DateTimeOffset ResolveRaisedAt(Goal goal, TaskId? taskId)
    {
        var candidates = goal.Timeline.Where(evt => taskId is null || evt.TaskId == taskId || evt.TaskId is null);
        return candidates
            .OrderByDescending(evt => evt.OccurredAt)
            .Select(evt => (DateTimeOffset?)evt.OccurredAt)
            .FirstOrDefault()
            ?? DateTimeOffset.UnixEpoch;
    }

    private static void Add(Dictionary<string, OperatorInboxItem> items, OperatorInboxItem item)
    {
        items.TryAdd(item.Id, item);
    }

    private static void SendEscalations(IOperatorChannel? channel, IReadOnlyList<OperatorInboxItem> items)
    {
        if (channel is null or NullOperatorChannel)
        {
            return;
        }

        foreach (var item in items)
        {
            var escalation = OperatorEscalationProjection.Project(item);
            if (escalation is null)
            {
                continue;
            }

            try
            {
                channel.SendEscalationAsync(escalation).GetAwaiter().GetResult();
            }
            catch
            {
                // The persisted inbox item is authoritative; channel delivery is best-effort.
            }
        }
    }

    private static List<Goal> ResolveGoals(AgentOrchestratorKernel kernel, string? goalPrefix)
    {
        if (!string.IsNullOrWhiteSpace(goalPrefix))
        {
            return [OrchestratorEntityResolver.ResolveGoal(kernel, null, goalPrefix)];
        }

        return kernel.Goals
            .Where(goal => goal.Status is not GoalStatus.Parked and not GoalStatus.Cancelled and not GoalStatus.Superseded)
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

    private static string BuildEscalationCommand(string goalPrefix, string integrationBranch)
    {
        if (!integrationBranch.StartsWith("conductor:", StringComparison.OrdinalIgnoreCase))
        {
            return $"land {goalPrefix}";
        }

        var state = integrationBranch["conductor:".Length..];
        return state switch
        {
            "Failed" or "Blocked" or "AwaitingHumanInput" => $"recover {goalPrefix} <note>",
            "Verified" => $"acceptance {goalPrefix} --autonomy supervised-auto",
            "Running" or "AwaitingVerification" => $"conduct {goalPrefix} --watch --poll-seconds 30",
            _ => $"conduct {goalPrefix}"
        };
    }

    private static string OwnershipHoldKey(string goalId, string taskId) =>
        $"ownership-hold:{goalId}:{taskId}:OwnershipHold";

    private static IReadOnlyList<OwnershipHoldRecord> LoadOwnershipHolds(OrchestratorWorkspace workspace)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, OwnershipHoldFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<OwnershipHoldStore>(File.ReadAllText(path), JsonOptions)?.Items ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<HoldFailureRecord> LoadHoldFailures(OrchestratorWorkspace workspace)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, HoldFailureFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<HoldFailureStore>(File.ReadAllText(path), JsonOptions)?.Items ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void SaveOwnershipHoldsWithRetry(OrchestratorWorkspace workspace, IReadOnlyList<OwnershipHoldRecord> items)
    {
        ExecuteWithHoldWriteRetry(() =>
        {
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            HoldStoreWriteAllText(
                Path.Combine(workspace.OrchestratorDirectory, OwnershipHoldFileName),
                JsonSerializer.Serialize(new OwnershipHoldStore(items), JsonOptions));
        });
    }

    private static void SaveHoldFailuresWithRetry(OrchestratorWorkspace workspace, IReadOnlyList<HoldFailureRecord> items)
    {
        ExecuteWithHoldWriteRetry(() =>
        {
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            HoldStoreWriteAllText(
                Path.Combine(workspace.OrchestratorDirectory, HoldFailureFileName),
                JsonSerializer.Serialize(new HoldFailureStore(items), JsonOptions));
        });
    }

    private static void ExecuteWithHoldWriteRetry(Action write)
    {
        var delay = InitialHoldWriteBackoff;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception ex) when (
                attempt < MaxHoldWriteRetries &&
                ex is IOException or UnauthorizedAccessException)
            {
                HoldWriteBackoff(delay);
                var doubled = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
                delay = doubled > MaxHoldWriteBackoff ? MaxHoldWriteBackoff : doubled;
            }
        }
    }

    private static void RecordHoldPersistenceFailure(
        OrchestratorWorkspace workspace,
        Goal goal,
        IReadOnlyList<OwnershipHoldRequest> requests,
        Exception exception)
    {
        var now = DateTimeOffset.UtcNow;
        var failures = LoadHoldFailures(workspace).ToList();
        foreach (var request in requests)
        {
            var key = $"hold-persistence-failure:{goal.Id.Value}:{request.TaskId.Value}";
            failures.RemoveAll(record =>
                record.ResolvedAt is null &&
                record.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            failures.Add(new HoldFailureRecord(
                BuildStoredId(goal.Id, OperatorInboxKind.HoldPersistenceFailure, key, now),
                OperatorInboxKind.HoldPersistenceFailure,
                key,
                goal.Id.Value,
                request.TaskId.Value,
                request.TaskNumber,
                "Ownership hold persistence failed",
                $"The system could not persist an ownership hold for task {request.TaskNumber}.",
                $"paths={string.Join(", ", request.ApprovalPaths)}; error={exception.GetType().Name}: {exception.Message}",
                now,
                null));
        }

        SaveHoldFailuresWithRetry(workspace, failures);
        AppendConductEvent(workspace, "HOLD_PERSISTENCE_FAILED", goal.Id.Value[..8],
            $"HOLD_PERSISTENCE_FAILED goal={goal.Id.Value[..8]} tasks={string.Join(",", requests.Select(r => r.TaskNumber))} error={exception.GetType().Name}");
    }

    private static void RecordHoldClearanceFailure(
        OrchestratorWorkspace workspace,
        Goal goal,
        OwnershipHoldRecord hold,
        string triggeringCommand,
        Exception exception)
    {
        var now = DateTimeOffset.UtcNow;
        var key = $"hold-clearance-failure:{goal.Id.Value}:{hold.Id}";
        var failures = LoadHoldFailures(workspace)
            .Where(record => !record.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        failures.Add(new HoldFailureRecord(
            BuildStoredId(goal.Id, OperatorInboxKind.HoldClearanceFailure, key, now),
            OperatorInboxKind.HoldClearanceFailure,
            key,
            goal.Id.Value,
            hold.TaskId,
            hold.TaskNumber,
            "Ownership hold clearance failed",
            $"Landing proceeded, but ownership hold {hold.Id} could not be cleared.",
            $"trigger={triggeringCommand}; error={exception.GetType().Name}: {exception.Message}",
            now,
            null));
        SaveHoldFailuresWithRetry(workspace, failures);
        AppendConductEvent(workspace, "HOLD_CLEARANCE_FAILED", goal.Id.Value[..8],
            $"HOLD_CLEARANCE_FAILED goal={goal.Id.Value[..8]} hold={hold.Id} command=\"{triggeringCommand}\" error={exception.GetType().Name}");
    }

    private static void AppendConductEvent(OrchestratorWorkspace workspace, string eventKind, string goalId, string detail)
    {
        try
        {
            new ConductEventLogWriter(workspace.ConductEventsLogPath).Append(eventKind, goalId, detail);
        }
        catch
        {
            // The inbox item is the primary loud signal.
        }
    }

    private static string BuildEscalationSuggestedAction(string integrationBranch)
    {
        if (!integrationBranch.StartsWith("conductor:", StringComparison.OrdinalIgnoreCase))
        {
            return "Review the change and promote manually or rerun land after resolving the issue.";
        }

        var state = integrationBranch["conductor:".Length..];
        return state switch
        {
            "Failed" or "Blocked" => "Recover the goal with an operator note, then resume the conductor.",
            "AwaitingHumanInput" => "Provide the requested input or recover the goal with an operator note.",
            "Verified" => "Review the goal branch diff and run acceptance when the landing risk is acceptable.",
            "Running" or "AwaitingVerification" => "Refresh or watch the conductor until worker and verification evidence settles.",
            _ => "Inspect the reason and run the suggested conductor command from the repository root."
        };
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

    private static string BuildStoredId(GoalId goalId, OperatorInboxKind kind, string sourceKey, DateTimeOffset raisedAt) =>
        BuildId(goalId, kind, $"{sourceKey}:{raisedAt.UtcTicks}");

    private sealed record OperatorInboxAcknowledgement(string ItemId, DateTimeOffset AcknowledgedAt, string? Note);

    private sealed record OperatorInboxAcknowledgementStore(IReadOnlyList<OperatorInboxAcknowledgement> Items);

    private sealed record LandingEscalationRecord(
        string GoalId,
        string Reason,
        string IntegrationBranch,
        DateTimeOffset EscalatedAt);

    private sealed record LandingEscalationStore(IReadOnlyList<LandingEscalationRecord> Items);

    private sealed record OwnershipHoldRecord(
        string Id,
        OperatorInboxKind Kind,
        string Key,
        string GoalId,
        string TaskId,
        int TaskNumber,
        AgentRole Role,
        IReadOnlyList<string> ApprovalPaths,
        string Title,
        string Message,
        string Evidence,
        DateTimeOffset RaisedAt,
        DateTimeOffset? ResolvedAt,
        string? ClearanceTriggeringCommand);

    private sealed record OwnershipHoldStore(IReadOnlyList<OwnershipHoldRecord> Items);

    private sealed record HoldFailureRecord(
        string Id,
        OperatorInboxKind Kind,
        string Key,
        string GoalId,
        string? TaskId,
        int? TaskNumber,
        string Title,
        string Message,
        string Evidence,
        DateTimeOffset RaisedAt,
        DateTimeOffset? ResolvedAt);

    private sealed record HoldFailureStore(IReadOnlyList<HoldFailureRecord> Items);
}
