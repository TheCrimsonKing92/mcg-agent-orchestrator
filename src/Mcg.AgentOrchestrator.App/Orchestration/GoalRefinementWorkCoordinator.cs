using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalRefinementWorkReceipt(int Version, string GoalId);

internal sealed record GoalRefinementWorkProcessResult(
    string GoalId,
    bool Claimed,
    bool Attached);

internal sealed record GoalRefinementWorkLaunchResult(
    bool Started,
    int? ProcessId,
    string Detail);

/// <summary>
/// Owns refinement after intake commits. The state outbox is the durable, leased work queue;
/// the detached process is only an executor and may be replaced after a crash.
/// </summary>
internal static partial class GoalRefinementWorkCoordinator
{
    internal const string OutboxKind = "goal-spec-refinement";
    internal const string CommandName = "goal-refinement-run";
    internal const string PendingPolicyReceipt =
        "spec_refinement outcome=pending owner=durable-outbox consumers_held=true researcher_allowed=true";
    private const int ReceiptVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static readonly TimeSpan RecoveryLaunchCadence = TimeSpan.FromMinutes(15);
    internal const int ConsecutiveFailedClaimLimit = 3;
    internal const string CadenceDeferredDetail = "executor-launch-deferred-cadence";
    internal const string ClaimInProgressDetail = "executor-claim-in-progress";

    private static readonly AsyncLocal<Func<OrchestratorWorkspace, GoalId, GoalRefinementWorkLaunchResult>?> LaunchOverrideLocal = new();
    internal static Func<OrchestratorWorkspace, GoalId, GoalRefinementWorkLaunchResult>? LaunchOverride
    {
        get => LaunchOverrideLocal.Value;
        set => LaunchOverrideLocal.Value = value;
    }
    internal static Func<DateTimeOffset>? UtcNowOverride { get; set; }

    internal static void ResetRecoveryLaunchesForTests()
    {
        // Launch attempts are workspace-scoped durable records now; temp workspaces isolate tests.
    }

    private static DateTimeOffset UtcNow() => UtcNowOverride?.Invoke() ?? DateTimeOffset.UtcNow;

    public static OrchestratorStateOutboxMessage CreateMessage(GoalId goalId) =>
        new(
            MessageId(goalId),
            OutboxKind,
            JsonSerializer.Serialize(new GoalRefinementWorkReceipt(ReceiptVersion, goalId.Value), JsonOptions),
            DateTimeOffset.UtcNow);

    public static string MessageId(GoalId goalId) => $"{OutboxKind}:{goalId.Value}";

    public static void RecordPending(AgentOrchestratorKernel kernel, GoalId goalId) =>
        kernel.RecordGoalPolicyDecision(goalId, PendingPolicyReceipt);

    public static bool HasPendingWork(Goal goal) =>
        goal.RefinedSpec is null &&
        goal.Timeline.Any(entry =>
            entry.Kind == ProgressKind.GoalPolicyDecision &&
            entry.Message.Equals(PendingPolicyReceipt, StringComparison.Ordinal));

    public static GoalRefinementWorkReceipt Deserialize(OrchestratorStateOutboxMessage message)
    {
        if (!message.Kind.Equals(OutboxKind, StringComparison.Ordinal))
            throw new JsonException($"Expected outbox kind '{OutboxKind}', found '{message.Kind}'.");

        var receipt = JsonSerializer.Deserialize<GoalRefinementWorkReceipt>(message.PayloadJson, JsonOptions)
            ?? throw new JsonException("Goal-refinement work receipt payload is empty.");
        if (receipt.Version != ReceiptVersion ||
            string.IsNullOrWhiteSpace(receipt.GoalId) ||
            !MessageId(new GoalId(receipt.GoalId)).Equals(message.Id, StringComparison.Ordinal))
        {
            throw new JsonException("Goal-refinement work receipt identity or version is invalid.");
        }

        return receipt;
    }

    public static async Task<GoalRefinementWorkProcessResult> ProcessAsync(
        IOrchestratorStateOutboxRepository repository,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        WorkerProfileCatalog workerProfiles,
        GoalId goalId,
        CancellationToken cancellationToken = default,
        string? rawOutputStamp = null)
    {
        var attached = false;
        IReadOnlyList<ProgressEventSnapshot> committedRefinementEvents = [];
        GoalLifecycleEventWriter? committedEventWriter = null;
        var claimed = await repository.TryProcessOutboxMessageAsync(
            MessageId(goalId),
            async (message, claimCancellationToken) =>
            {
                var phase = "deserialize-receipt";
                GoalRefinementWorkReceipt receipt;
                try
                {
                    receipt = Deserialize(message);
                }
                catch (JsonException ex)
                {
                    return OrchestratorStateOutboxProcessingResult.Quarantined(ex.Message);
                }

                if (!receipt.GoalId.Equals(goalId.Value, StringComparison.Ordinal))
                {
                    return OrchestratorStateOutboxProcessingResult.Quarantined(
                        $"Goal-refinement work receipt targets '{receipt.GoalId}', not '{goalId.Value}'.");
                }

                try
                {
                    phase = "load-goal";
                    var refinementKernel = await repository.LoadAsync(claimCancellationToken).ConfigureAwait(false);
                    var goal = refinementKernel.Goals.FirstOrDefault(candidate => candidate.Id == goalId);
                    if (goal is null)
                    {
                        return OrchestratorStateOutboxProcessingResult.Quarantined(
                            $"Goal-refinement work targets missing goal '{goalId.Value}'.");
                    }

                    phase = "create-service";
                    var service = CreateService(workspace, providers, workerProfiles, rawOutputStamp);
                    var eventWriter = new GoalLifecycleEventWriter(
                        workspace.GoalLifecycleEventsDirectory,
                        kernel: refinementKernel, integrationBranch: workspace.IntegrationBranch);
                    var baselineSnapshot = refinementKernel.ExportGoalSnapshot(goalId);
                    var synchronizingAnswers = goal.RefinedSpec is not null;

                    if (!synchronizingAnswers)
                    {
                        phase = "load-refinement-policy";
                        var selection = LoadRefinementPolicy(workspace);
                        if (selection.FallbackDecision is not null)
                            refinementKernel.RecordGoalPolicyDecision(goalId, selection.FallbackDecision);
                        phase = "provider-refinement";
                        var refinement = await service.RefineAsync(
                                refinementKernel,
                                goalId,
                                policy: selection.Policy,
                                cancellationToken: claimCancellationToken)
                            .ConfigureAwait(false);
                        phase = "record-policy-receipt";
                        refinementKernel.RecordGoalPolicyDecision(
                            goalId,
                            GoalRefinementGate.BuildPolicyReceipt(refinement, selection.Policy.Name));
                    }
                    else
                    {
                        phase = "sync-feasibility-answers";
                        _ = await service.SyncAnsweredFeasibilityClarificationsAsync(
                                refinementKernel,
                                goalId,
                                claimCancellationToken)
                            .ConfigureAwait(false);
                        phase = "sync-ordinary-answers";
                        _ = await service.SyncAnsweredClarificationsAsync(
                                refinementKernel,
                                goalId,
                                claimCancellationToken)
                            .ConfigureAwait(false);
                    }

                    var refinedSnapshot = refinementKernel.ExportGoalSnapshot(goalId);
                    if (refinedSnapshot.RefinedSpec?.OpenQuestions.Any(question =>
                            string.Equals(question.Status, "Open", StringComparison.OrdinalIgnoreCase)) == true)
                    {
                        phase = "emit-clarification-event";
                        eventWriter.AppendClarificationNeeded(goalId, "spec");
                        refinedSnapshot = refinementKernel.ExportGoalSnapshot(goalId);
                    }

                    var changed = !Equals(baselineSnapshot.RefinedSpec, refinedSnapshot.RefinedSpec) ||
                        baselineSnapshot.RefinedSpecVersions.Count != refinedSnapshot.RefinedSpecVersions.Count ||
                        baselineSnapshot.Timeline.Count != refinedSnapshot.Timeline.Count;
                    if (!changed)
                        return OrchestratorStateOutboxProcessingResult.Completed;

                    phase = "attach-snapshot";
                    attached = await repository.TransactGoalAsync(
                        goalId,
                        (current, _) =>
                        {
                            if (current is null)
                            {
                                throw new InvalidOperationException(
                                    $"Goal '{goalId.Value}' disappeared before refined-spec attachment.");
                            }

                            if (!synchronizingAnswers && current.RefinedSpec is not null)
                                return Task.FromResult((false, current, false));

                            var refinementEvents = refinedSnapshot.Timeline
                                .Skip(baselineSnapshot.Timeline.Count)
                                .ToArray();
                            var storedVersion = current.RefinedSpecVersions?.Max(item => (int?)item.Version) ?? 0;
                            var obligations = (current.CriterionEvidenceObligations ?? []).ToList();
                            var storedObligationIds = obligations.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                            foreach (var obligation in refinedSnapshot.CriterionEvidenceObligations ?? [])
                            {
                                if (obligation.CriterionVersion > storedVersion && storedObligationIds.Add(obligation.Id))
                                    obligations.Add(obligation);
                            }
                            var merged = current with
                            {
                                RefinedSpec = refinedSnapshot.RefinedSpec,
                                RefinedSpecVersions = refinedSnapshot.RefinedSpecVersions,
                                CriterionEvidenceObligations = obligations,
                                ClarificationRoundCount = Math.Max(
                                    current.ClarificationRoundCount,
                                    refinedSnapshot.ClarificationRoundCount),
                                Timeline = current.Timeline.Concat(refinementEvents).ToArray()
                            };
                            committedRefinementEvents = refinementEvents;
                            committedEventWriter = eventWriter;
                            return Task.FromResult((true, merged, true));
                        },
                        claimCancellationToken).ConfigureAwait(false);

                    return OrchestratorStateOutboxProcessingResult.Completed;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"SPEC_REFINEMENT_FAILED owner=durable-outbox phase={phase} " +
                        $"detail={SingleLine(ex.Message)}",
                        ex);
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (attached)
            GoalEventsTimelineMirror.AppendMissing(committedEventWriter!, goalId, committedRefinementEvents, "spec-refinement");

        if (claimed)
            SpecRefinementLaunchAttemptStore.ForWorkspace(workspace).Reset(goalId);

        return new GoalRefinementWorkProcessResult(goalId.Value, claimed, attached);
    }

    public static bool TryEnsurePendingOutbox(OrchestratorWorkspace workspace, GoalId goalId)
    {
        var message = CreateMessage(goalId);
        var inserted = false;
        if (StateDbWriteSession.TryExecute(
                workspace.SqliteStatePath,
                connection => inserted = SqliteOrchestratorStateRepository.TryInsertOutboxMessageIfMissing(
                    connection,
                    message)))
        {
            return inserted;
        }

        return EnsurePendingOutboxOn(
            new SqliteOrchestratorStateRepository(workspace.SqliteStatePath),
            goalId);
    }

    public static GoalRefinementWorkLaunchResult TryLaunchIfDue(
        OrchestratorWorkspace workspace,
        GoalId goalId,
        OrchestratorStateOutboxStatus outboxStatus = OrchestratorStateOutboxStatus.Pending,
        DateTimeOffset? processingStartedAt = null)
    {
        var now = UtcNow();
        var store = SpecRefinementLaunchAttemptStore.ForWorkspace(workspace);
        if (outboxStatus == OrchestratorStateOutboxStatus.Processing &&
            processingStartedAt is { } startedAt &&
            now - startedAt < SqliteOrchestratorStateRepository.OutboxProcessingLease)
        {
            store.Reset(goalId);
            return new GoalRefinementWorkLaunchResult(false, null, ClaimInProgressDetail);
        }

        var attempt = store.Get(goalId);
        var decision = SpecRefinementLaunchPolicy.Decide(
            attempt,
            now,
            RecoveryLaunchCadence,
            ConsecutiveFailedClaimLimit);
        if (decision.Kind == SpecRefinementLaunchDecisionKind.DeferCadence)
        {
            return new GoalRefinementWorkLaunchResult(false, null, CadenceDeferredDetail);
        }

        var messageId = MessageId(goalId);
        var statePath = Path.GetFullPath(workspace.SqliteStatePath);
        if (decision.Kind == SpecRefinementLaunchDecisionKind.Escalated)
        {
            if (attempt?.EscalatedAt is null)
            {
                attempt = new SpecRefinementLaunchAttempt(
                    attempt?.LastLaunchAt ?? now,
                    decision.ConsecutiveFailedClaims,
                    now,
                    messageId,
                    statePath);
                store.Save(goalId, attempt);
                new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, integrationBranch: workspace.IntegrationBranch).AppendGoalEscalated(
                    goalId,
                    GoalLifecycleState.WorkspaceReady,
                    BuildEscalationDetail(goalId, decision.ConsecutiveFailedClaims, messageId, statePath),
                    "spec-refinement-launch");
            }

            return new GoalRefinementWorkLaunchResult(
                false,
                null,
                BuildEscalationDetail(goalId, decision.ConsecutiveFailedClaims, messageId, statePath));
        }

        var launch = TryLaunch(workspace, goalId);
        if (launch.Started)
        {
            store.Save(
                goalId,
                new SpecRefinementLaunchAttempt(
                    now,
                    decision.ConsecutiveFailedClaims,
                    EscalatedAt: null,
                    messageId,
                    statePath));
        }
        return launch;
    }

    internal static int GetConsecutiveFailedClaims(OrchestratorWorkspace workspace, GoalId goalId) =>
        SpecRefinementLaunchAttemptStore.ForWorkspace(workspace).Get(goalId)?.ConsecutiveFailedClaims ?? 0;

    internal static string FormatPendingAge(DateTimeOffset createdAt)
    {
        var age = UtcNow() - createdAt;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalHours >= 1)
            return $"{(long)age.TotalHours}h{age.Minutes:D2}m";
        if (age.TotalMinutes >= 1)
            return $"{(long)age.TotalMinutes}m{age.Seconds:D2}s";
        return $"{(long)age.TotalSeconds}s";
    }

    public static GoalRefinementWorkLaunchResult TryLaunch(OrchestratorWorkspace workspace, GoalId goalId)
    {
        if (LaunchOverride is { } launchOverride)
            return launchOverride(workspace, goalId);

        if (!string.Equals(
                Assembly.GetEntryAssembly()?.GetName().Name,
                "Mcg.AgentOrchestrator.App",
                StringComparison.Ordinal))
        {
            return new GoalRefinementWorkLaunchResult(false, null, "executor-launch-deferred-outside-app-host");
        }

        try
        {
            Directory.CreateDirectory(workspace.LogDirectory);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            var request = CreateLaunchRequest(workspace, goalId, stamp);
            var launched = ConductorLoopHandoff.LaunchDetached(request);
            return new GoalRefinementWorkLaunchResult(true, launched.ProcessId, launched.LaunchDetail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GoalRefinementWorkLaunchResult(
                false,
                null,
                $"executor-launch-failed:{ex.GetType().Name}:{SingleLine(ex.Message)}");
        }
    }

    internal static ConductLoopLaunchRequest CreateLaunchRequest(
        OrchestratorWorkspace workspace,
        GoalId goalId,
        string stamp)
    {
        var prefix = goalId.Value[..Math.Min(8, goalId.Value.Length)];
        var args = new List<string>();
        if (workspace.IsProjectScoped)
            args.Add($"--project={workspace.ProjectName}");
        if (workspace.IsTenantScoped)
            args.Add($"--tenant={workspace.TenantName}");
        args.Add(CommandName);
        args.Add(goalId.Value);
        args.Add(stamp);

        return new ConductLoopLaunchRequest(
            $"spec-refinement-{prefix}",
            args,
            Path.Combine(workspace.LogDirectory, $"spec-refinement-{prefix}-{stamp}.out.log"),
            Path.Combine(workspace.LogDirectory, $"spec-refinement-{prefix}-{stamp}.err.log"),
            workspace.RootDirectory,
            RenewalCount: 0);
    }

    public static void TryLaunchFirstPending(
        IOrchestratorStateOutboxRepository repository,
        OrchestratorWorkspace workspace)
    {
        var pending = repository.ListOutboxMessagesAsync(OutboxKind).GetAwaiter().GetResult().FirstOrDefault();
        if (pending is null)
        {
            var kernel = repository.LoadAsync().GetAwaiter().GetResult();
            foreach (var goal in kernel.Goals.Where(HasPendingWork))
                EnsurePendingOutboxOn(repository, goal.Id);

            pending = repository.ListOutboxMessagesAsync(OutboxKind).GetAwaiter().GetResult().FirstOrDefault();
            if (pending is null)
                return;
        }

        try
        {
            var receipt = Deserialize(pending);
            var state = repository.GetOutboxStateAsync(pending.Id).GetAwaiter().GetResult();
            _ = TryLaunchIfDue(
                workspace,
                new GoalId(receipt.GoalId),
                state?.Status ?? OrchestratorStateOutboxStatus.Pending,
                state?.ProcessingStartedAt);
        }
        catch (JsonException)
        {
            // The processing path quarantines poison receipts. Recovery launch must stay non-blocking.
        }
    }

    private static bool EnsurePendingOutboxOn(
        IOrchestratorStateOutboxRepository repository,
        GoalId goalId)
    {
        var messageId = MessageId(goalId);
        var existing = repository.ListOutboxMessagesAsync(OutboxKind).GetAwaiter().GetResult();
        if (existing.Any(message => message.Id.Equals(messageId, StringComparison.Ordinal)))
            return false;

        repository.TransactWithOutboxAsync(
                (_, _) => Task.FromResult((
                    ShouldSave: false,
                    Result: true,
                    OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[CreateMessage(goalId)])))
            .GetAwaiter()
            .GetResult();
        return true;
    }

    private static string SingleLine(string value) =>
        string.Concat(value.Select(character => char.IsWhiteSpace(character) ? ' ' : character)).Trim();

    private static string BuildEscalationDetail(
        GoalId goalId,
        int failures,
        string messageId,
        string statePath) =>
        $"SPEC_REFINEMENT_OPERATOR_RECOVERY goal={goalId.Value} reason=claim-miss " +
        $"message_id={messageId} store_path={statePath} consecutive_failed_claims={failures}";

    private static GoalRefinementService CreateService(
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        WorkerProfileCatalog workerProfiles,
        string? rawOutputStamp) =>
        new(
            providers,
            ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath),
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
            new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath),
            workerProfiles,
            rawOutputDirectory: workspace.LogDirectory,
            rawOutputStamp: rawOutputStamp);
}
