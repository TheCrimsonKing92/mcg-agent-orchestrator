using System.Collections.Concurrent;
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
internal static class GoalRefinementWorkCoordinator
{
    internal const string OutboxKind = "goal-spec-refinement";
    internal const string CommandName = "goal-refinement-run";
    internal const string PendingPolicyReceipt =
        "spec_refinement outcome=pending owner=durable-outbox consumers_held=true researcher_allowed=true";
    private const int ReceiptVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RecoveryLaunches = new(StringComparer.Ordinal);
    internal static readonly TimeSpan RecoveryLaunchCadence = TimeSpan.FromMinutes(15);
    internal const string CadenceDeferredDetail = "executor-launch-deferred-cadence";

    internal static Func<OrchestratorWorkspace, GoalId, GoalRefinementWorkLaunchResult>? LaunchOverride { get; set; }
    internal static Func<DateTimeOffset>? UtcNowOverride { get; set; }

    internal static void ResetRecoveryLaunchesForTests() => RecoveryLaunches.Clear();

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
        CancellationToken cancellationToken = default)
    {
        var attached = false;
        var claimed = await repository.TryProcessOutboxMessageAsync(
            MessageId(goalId),
            async (message, claimCancellationToken) =>
            {
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

                var refinementKernel = await repository.LoadAsync(claimCancellationToken).ConfigureAwait(false);
                var goal = refinementKernel.Goals.FirstOrDefault(candidate => candidate.Id == goalId);
                if (goal is null)
                {
                    return OrchestratorStateOutboxProcessingResult.Quarantined(
                        $"Goal-refinement work targets missing goal '{goalId.Value}'.");
                }

                if (goal.RefinedSpec is not null)
                    return OrchestratorStateOutboxProcessingResult.Completed;

                var baselineSnapshot = refinementKernel.ExportGoalSnapshot(goalId);
                _ = GoalRefinementGate.EnsureRefined(
                    refinementKernel,
                    workspace,
                    providers,
                    goal,
                    workerProfiles: workerProfiles,
                    eventWriter: new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, kernel: refinementKernel));
                var refinedSnapshot = refinementKernel.ExportGoalSnapshot(goalId);

                attached = await repository.TransactGoalAsync(
                    goalId,
                    (current, _) =>
                    {
                        if (current is null)
                        {
                            throw new InvalidOperationException(
                                $"Goal '{goalId.Value}' disappeared before refined-spec attachment.");
                        }

                        if (current.RefinedSpec is not null)
                            return Task.FromResult((false, current, false));

                        var refinementEvents = refinedSnapshot.Timeline
                            .Skip(baselineSnapshot.Timeline.Count)
                            .ToArray();
                        var merged = current with
                        {
                            RefinedSpec = refinedSnapshot.RefinedSpec,
                            RefinedSpecVersions = refinedSnapshot.RefinedSpecVersions,
                            ClarificationRoundCount = Math.Max(
                                current.ClarificationRoundCount,
                                refinedSnapshot.ClarificationRoundCount),
                            Timeline = current.Timeline.Concat(refinementEvents).ToArray()
                        };
                        return Task.FromResult((true, merged, true));
                    },
                    claimCancellationToken).ConfigureAwait(false);

                return OrchestratorStateOutboxProcessingResult.Completed;
            },
            cancellationToken).ConfigureAwait(false);

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

    public static GoalRefinementWorkLaunchResult TryLaunchIfDue(OrchestratorWorkspace workspace, GoalId goalId)
    {
        var now = UtcNow();
        if (RecoveryLaunches.TryGetValue(goalId.Value, out var lastLaunch) &&
            now - lastLaunch < RecoveryLaunchCadence)
        {
            return new GoalRefinementWorkLaunchResult(false, null, CadenceDeferredDetail);
        }

        var launch = TryLaunch(workspace, goalId);
        if (launch.Started)
            RecoveryLaunches[goalId.Value] = now;
        return launch;
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
            var prefix = goalId.Value[..Math.Min(8, goalId.Value.Length)];
            var args = new List<string>();
            if (workspace.IsProjectScoped)
                args.Add($"--project={workspace.ProjectName}");
            if (workspace.IsTenantScoped)
                args.Add($"--tenant={workspace.TenantName}");
            args.Add(CommandName);
            args.Add(goalId.Value);

            var request = new ConductLoopLaunchRequest(
                $"spec-refinement-{prefix}",
                args,
                Path.Combine(workspace.LogDirectory, $"spec-refinement-{prefix}-{stamp}.out.log"),
                Path.Combine(workspace.LogDirectory, $"spec-refinement-{prefix}-{stamp}.err.log"),
                workspace.RootDirectory,
                RenewalCount: 0);
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
            _ = TryLaunchIfDue(workspace, new GoalId(receipt.GoalId));
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
}
