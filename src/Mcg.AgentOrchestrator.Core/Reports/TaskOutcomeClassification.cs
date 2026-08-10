namespace Mcg.AgentOrchestrator.Core;

public enum TaskOutcomeClass
{
    Success,
    ReconciledToSuccess,
    RealFailure,
    Environmental,
    ManufacturedFixed,
    UnknownEra
}

public sealed record TaskOutcomeClassification(string? Rule, TaskOutcomeClass Class)
{
    public static TaskOutcomeClassification Success(string? rule = null) => new(rule, TaskOutcomeClass.Success);
    public static TaskOutcomeClassification UnknownEra(string? rule = null) => new(rule, TaskOutcomeClass.UnknownEra);
}

internal sealed record TaskOutcomeRule(string Token, TaskOutcomeClass Class);

internal static class TaskOutcomeRules
{
    public static readonly TaskOutcomeRule CommittedWorkerResultEvidence = new("committed-worker-result-evidence", TaskOutcomeClass.Success);
    public static readonly TaskOutcomeRule VerifiedNoNewCommit = new("verified-no-new-commit", TaskOutcomeClass.Success);
    public static readonly TaskOutcomeRule SucceededDispatchCompletionEvidence = new("succeeded-dispatch-completion-evidence", TaskOutcomeClass.Success);

    public static readonly TaskOutcomeRule DirtyDispatchRecovery = new("dirty-dispatch-recovery", TaskOutcomeClass.Environmental);
    public static readonly TaskOutcomeRule PreflightFailure = new("preflight-failure", TaskOutcomeClass.Environmental);
    public static readonly TaskOutcomeRule ProviderAuthentication = new("provider-authentication", TaskOutcomeClass.Environmental);
    public static readonly TaskOutcomeRule ProviderConnectivity = new("provider-connectivity", TaskOutcomeClass.Environmental);
    public static readonly TaskOutcomeRule ProviderNeutralProgressStall = new("provider-neutral-progress-stall", TaskOutcomeClass.Environmental);
    public static readonly TaskOutcomeRule ProviderModelRejection = new("provider-model-rejection", TaskOutcomeClass.Environmental);
    public static readonly TaskOutcomeRule ProviderRateLimit = new("provider-rate-limit", TaskOutcomeClass.Environmental);
    public static readonly TaskOutcomeRule ProviderSandboxLaunch1312 = new("provider-sandbox-launch-1312", TaskOutcomeClass.Environmental);
    public static readonly TaskOutcomeRule SubscriptionLimit = new("subscription-limit", TaskOutcomeClass.Environmental);
    public static readonly TaskOutcomeRule SilentLaunchFailure = new("silent-launch-failure", TaskOutcomeClass.Environmental);

    public static readonly TaskOutcomeRule EmptyOutputFlake = new("empty-output-flake", TaskOutcomeClass.ManufacturedFixed);
    private static readonly TaskOutcomeRule ProviderSandbox1312 = new("provider-sandbox-1312", TaskOutcomeClass.ManufacturedFixed);
    public static readonly TaskOutcomeRule RetryRoundProducedNoCommitAndNoDeferral = new("retry-round-produced-no-commit-and-no-deferral", TaskOutcomeClass.ManufacturedFixed);
    public static readonly TaskOutcomeRule SandboxCommitBlocked = new("sandbox-commit-blocked", TaskOutcomeClass.ManufacturedFixed);

    public static readonly TaskOutcomeRule SucceededWorkerResultFailingTests = new("succeeded-worker-result-failing-tests", TaskOutcomeClass.RealFailure);
    public static readonly TaskOutcomeRule TesterWorkerResultBlocker = new("tester-worker-result-blocker", TaskOutcomeClass.RealFailure);
    public static readonly TaskOutcomeRule RealFailure = new("real-failure", TaskOutcomeClass.RealFailure);

    public static readonly TaskOutcomeRule ProviderUnknown = new("provider-unknown", TaskOutcomeClass.UnknownEra);
    public static readonly TaskOutcomeRule TesterVerificationInconclusive = new("tester-verification-inconclusive", TaskOutcomeClass.UnknownEra);
    public static readonly TaskOutcomeRule ResearcherOutputContractRejected = new("researcher-output-contract-rejected", TaskOutcomeClass.UnknownEra);
    public static readonly TaskOutcomeRule ResearcherArtifactPersistenceFailed = new("researcher-artifact-persistence-failed", TaskOutcomeClass.UnknownEra);
    public static readonly TaskOutcomeRule PlannerOutputContractRejected = new("planner-output-contract-rejected", TaskOutcomeClass.UnknownEra);
    public static readonly TaskOutcomeRule PlannerPlanPersistenceFailed = new("planner-plan-persistence-failed", TaskOutcomeClass.UnknownEra);
    public static readonly TaskOutcomeRule WorkerBuildCheckFailed = new("worker-build-check-failed", TaskOutcomeClass.UnknownEra);
    public static readonly TaskOutcomeRule WrapperProcessExitFailure = new("wrapper-process-exit-failure", TaskOutcomeClass.UnknownEra);
    public static readonly TaskOutcomeRule RequiredFileChangeEvidenceMissing = new("required-file-change-evidence-missing", TaskOutcomeClass.UnknownEra);
    public static readonly TaskOutcomeRule WorktreeInspectionFailed = new("worktree-inspection-failed", TaskOutcomeClass.UnknownEra);
    public static readonly TaskOutcomeRule UnknownFailure = new("unknown-failure", TaskOutcomeClass.UnknownEra);

    private static readonly TaskOutcomeRule RecoverableSubscriptionLimitLegacy = new("recoverable-subscription-limit", TaskOutcomeClass.Environmental);
    private static readonly TaskOutcomeRule ProviderSandbox1312Legacy = new("provider-sandbox1312", TaskOutcomeClass.ManufacturedFixed);

    public static IReadOnlyList<TaskOutcomeRule> Produced { get; } =
    [
        CommittedWorkerResultEvidence,
        VerifiedNoNewCommit,
        SucceededDispatchCompletionEvidence,
        DirtyDispatchRecovery,
        PreflightFailure,
        ProviderAuthentication,
        ProviderConnectivity,
        ProviderNeutralProgressStall,
        ProviderModelRejection,
        ProviderRateLimit,
        ProviderSandboxLaunch1312,
        SubscriptionLimit,
        SilentLaunchFailure,
        EmptyOutputFlake,
        RetryRoundProducedNoCommitAndNoDeferral,
        SandboxCommitBlocked,
        SucceededWorkerResultFailingTests,
        TesterWorkerResultBlocker,
        RealFailure,
        ProviderUnknown,
        TesterVerificationInconclusive,
        ResearcherOutputContractRejected,
        ResearcherArtifactPersistenceFailed,
        PlannerOutputContractRejected,
        PlannerPlanPersistenceFailed,
        WorkerBuildCheckFailed,
        WrapperProcessExitFailure,
        RequiredFileChangeEvidenceMissing,
        WorktreeInspectionFailed,
        UnknownFailure
    ];

    public static IReadOnlyDictionary<string, TaskOutcomeRule> Known { get; } = Produced
        .Append(RecoverableSubscriptionLimitLegacy)
        .Append(ProviderSandbox1312)
        .Append(ProviderSandbox1312Legacy)
        .ToDictionary(rule => rule.Token, StringComparer.OrdinalIgnoreCase);
}

public static class TaskOutcomeClassifier
{
    private const string ClassifierPrefix = "CLASSIFIER ";
    private const string RulePrefix = "rule=";
    private const string OutcomeClassPrefix = "outcome_class=";

    public static TaskOutcomeClassification Classify(WorkTaskStatus outcome, string? rule)
    {
        var normalizedRule = NormalizeRule(rule);
        if (outcome == WorkTaskStatus.Completed)
        {
            return TaskOutcomeClassification.Success(normalizedRule);
        }

        if (outcome != WorkTaskStatus.Failed)
        {
            return TaskOutcomeClassification.UnknownEra(normalizedRule);
        }

        if (normalizedRule is null)
        {
            return TaskOutcomeClassification.UnknownEra();
        }

        if (TaskOutcomeRules.Known.TryGetValue(normalizedRule, out var knownRule))
        {
            return new TaskOutcomeClassification(normalizedRule, knownRule.Class);
        }

        return TaskOutcomeClassification.UnknownEra(normalizedRule);
    }

    public static TaskOutcomeClassification FromTimeline(
        IEnumerable<ProgressEvent> timeline,
        TaskId taskId,
        WorkTaskStatus outcome,
        DateTimeOffset? completedAt = null,
        DateTimeOffset? before = null)
    {
        var receipt = timeline
            .Select((evt, index) => (evt, index))
            .Where(item => item.evt.TaskId == taskId && item.evt.Kind is ProgressKind.TaskNote or ProgressKind.OperatorTaskNote)
            .Where(item => completedAt is null || item.evt.OccurredAt >= completedAt.Value)
            .Where(item => before is null || item.evt.OccurredAt <= before.Value)
            .OrderByDescending(item => item.evt.OccurredAt)
            .ThenByDescending(item => item.index)
            .Select(item => item.evt.Message)
            .FirstOrDefault(message => TryExtractRule(message) is not null || TryExtractClass(message) is not null);
        var rule = TryExtractRule(receipt);
        var recordedClass = TryExtractClass(receipt);
        if ((outcome == WorkTaskStatus.Failed && recordedClass is not null) ||
            (outcome == WorkTaskStatus.Completed && recordedClass == TaskOutcomeClass.ReconciledToSuccess))
        {
            return new TaskOutcomeClassification(rule, recordedClass!.Value);
        }

        return Classify(outcome, rule);
    }

    public static string? TryExtractRule(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            !message.Contains(ClassifierPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var ruleIndex = message.IndexOf(RulePrefix, StringComparison.OrdinalIgnoreCase);
        if (ruleIndex < 0)
        {
            return null;
        }

        var start = ruleIndex + RulePrefix.Length;
        var end = start;
        while (end < message.Length)
        {
            var c = message[end];
            if (!(char.IsLetterOrDigit(c) || c == '-'))
            {
                break;
            }

            end++;
        }

        return end > start ? NormalizeRule(message[start..end]) : null;
    }

    public static TaskOutcomeClass? TryExtractClass(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            !message.Contains(ClassifierPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var classIndex = message.IndexOf(OutcomeClassPrefix, StringComparison.OrdinalIgnoreCase);
        if (classIndex < 0)
        {
            return null;
        }

        var start = classIndex + OutcomeClassPrefix.Length;
        var end = start;
        while (end < message.Length)
        {
            var c = message[end];
            if (!(char.IsLetterOrDigit(c) || c == '-'))
            {
                break;
            }

            end++;
        }

        return end > start ? ParseClass(message[start..end]) : TaskOutcomeClass.UnknownEra;
    }

    public static string FormatClass(TaskOutcomeClass outcomeClass) =>
        outcomeClass switch
        {
            TaskOutcomeClass.Success => "success",
            TaskOutcomeClass.ReconciledToSuccess => "reconciled-to-success",
            TaskOutcomeClass.RealFailure => "real-failure",
            TaskOutcomeClass.Environmental => "environmental",
            TaskOutcomeClass.ManufacturedFixed => "manufactured-fixed",
            TaskOutcomeClass.UnknownEra => "unknown-era",
            _ => outcomeClass.ToString()
        };

    public static TaskOutcomeClass ParseClass(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "success" => TaskOutcomeClass.Success,
            "reconciled-to-success" => TaskOutcomeClass.ReconciledToSuccess,
            "real-failure" => TaskOutcomeClass.RealFailure,
            "environmental" => TaskOutcomeClass.Environmental,
            "manufactured-fixed" => TaskOutcomeClass.ManufacturedFixed,
            "unknown-era" => TaskOutcomeClass.UnknownEra,
            _ => TaskOutcomeClass.UnknownEra
        };
    }

    private static string? NormalizeRule(string? rule)
    {
        var trimmed = rule?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.ToLowerInvariant();
    }
}
