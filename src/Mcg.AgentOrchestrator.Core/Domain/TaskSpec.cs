namespace Mcg.AgentOrchestrator.Core;

public sealed class TaskSpec
{
    private readonly List<TaskVerificationRecord> _verificationHistory = [];

    public TaskSpec(TaskId id, string description, AgentRole requiredRole, string? verificationPlan = null)
    {
        Id = id;
        Description = RequireText(description, nameof(description));
        RequiredRole = requiredRole;
        VerificationPlan = string.IsNullOrWhiteSpace(verificationPlan) ? null : verificationPlan.Trim();
    }

    public TaskId Id { get; }

    public string Description { get; }

    public AgentRole RequiredRole { get; }

    public string? VerificationPlan { get; private set; }

    public DateTimeOffset? SubscriptionRetryAfter { get; private set; }

    public string? SubscriptionLimitReviewNote { get; private set; }

    public DateTimeOffset? SubscriptionLimitReviewedAt { get; private set; }

    public int SubscriptionLimitReviewedFailureCount { get; private set; }

    public int CriterionRetryCount { get; private set; }

    public IReadOnlyList<string> CriterionRetryFeedback { get; private set; } = [];

    public WorkTaskStatus Status { get; private set; } = WorkTaskStatus.Pending;

    public AgentId? AssignedAgentId { get; private set; }

    public TaskExecutionRecord? LastExecution { get; private set; }

    public TaskVerificationRecord? LastVerification { get; private set; }

    public IReadOnlyList<TaskVerificationRecord> VerificationHistory => _verificationHistory;

    public TaskDispatchRecord? LastDispatch { get; private set; }

    public TaskProcessRecord? LastProcess { get; private set; }

    internal void AssignTo(AgentId agentId)
    {
        AssignedAgentId = agentId;
        Status = WorkTaskStatus.Assigned;
    }

    internal void SetStatus(WorkTaskStatus status) => Status = status;

    internal TaskSnapshot ToSnapshot()
    {
        return new TaskSnapshot(
            Id.Value,
            Description,
            RequiredRole,
            Status,
            AssignedAgentId?.Value,
            LastExecution is null
                ? null
                : new TaskExecutionSnapshot(
                    LastExecution.AgentId.Value,
                    LastExecution.AgentName,
                    LastExecution.ProviderName,
                    LastExecution.ModelName,
                    LastExecution.Output,
                    LastExecution.StopReason,
                    LastExecution.Usage?.InputTokens,
                    LastExecution.Usage?.OutputTokens,
                    LastExecution.CompletedAt,
                    LastExecution.TaskComplexity,
                    LastExecution.MaxOutputTokens,
                    LastExecution.PromptCharacterCount),
            LastVerification is null
                ? null
                : new TaskVerificationSnapshot(
                    LastVerification.Command,
                    LastVerification.WorkingDirectory,
                    LastVerification.ExitCode,
                    VerificationTextBounds.BoundText(LastVerification.StandardOutput, LastVerification.StandardOutputPath),
                    VerificationTextBounds.BoundText(LastVerification.StandardError, LastVerification.StandardErrorPath),
                    LastVerification.CompletedAt,
                    LastVerification.ModelFitNote,
                    LastVerification.StandardOutputPath,
                    LastVerification.StandardErrorPath),
            _verificationHistory
                .Select(verification => new TaskVerificationSnapshot(
                    verification.Command,
                    verification.WorkingDirectory,
                    verification.ExitCode,
                    VerificationTextBounds.BoundText(verification.StandardOutput, verification.StandardOutputPath),
                    VerificationTextBounds.BoundText(verification.StandardError, verification.StandardErrorPath),
                    verification.CompletedAt,
                    verification.ModelFitNote,
                    verification.StandardOutputPath,
                    verification.StandardErrorPath))
                .ToList(),
            LastDispatch is null
                ? null
                : new TaskDispatchSnapshot(
                    LastDispatch.WorkerName,
                    LastDispatch.Command,
                    LastDispatch.WorkingDirectory,
                    LastDispatch.DispatchedAt,
                    LastDispatch.ProviderName,
                    LastDispatch.ModelName,
                    LastDispatch.ReasoningEffort,
                    LastDispatch.TaskComplexity,
                    LastDispatch.PromptCharacterCount,
                    LastDispatch.UsesComplexModel),
            LastProcess is null
                ? null
                : new TaskProcessSnapshot(
                    LastProcess.ProcessId,
                    LastProcess.Command,
                    LastProcess.WorkingDirectory,
                    LastProcess.StandardOutputPath,
                    LastProcess.StandardErrorPath,
                    LastProcess.ExitCodePath,
                    LastProcess.StartedAt,
                    LastProcess.CompletedAt,
                    LastProcess.ExitCode,
                    LastProcess.WasCancelled,
                    LastProcess.TrackedProcessIds),
            VerificationPlan,
            SubscriptionRetryAfter,
            SubscriptionLimitReviewNote,
            SubscriptionLimitReviewedAt,
            SubscriptionLimitReviewedFailureCount,
            CriterionRetryCount,
            CriterionRetryFeedback);
    }

    internal static TaskSpec FromSnapshot(TaskSnapshot snapshot)
    {
        var task = new TaskSpec(new TaskId(snapshot.Id), snapshot.Description, snapshot.RequiredRole, snapshot.VerificationPlan);

        if (snapshot.AssignedAgentId is not null)
        {
            task.AssignTo(new AgentId(snapshot.AssignedAgentId));
        }

        task.SetStatus(snapshot.Status);

        if (snapshot.LastExecution is not null)
        {
            task.RecordExecution(new TaskExecutionRecord(
                new AgentId(snapshot.LastExecution.AgentId),
                snapshot.LastExecution.AgentName,
                snapshot.LastExecution.ProviderName,
                snapshot.LastExecution.ModelName,
                snapshot.LastExecution.Output,
                snapshot.LastExecution.StopReason,
                new ModelUsage(snapshot.LastExecution.InputTokens, snapshot.LastExecution.OutputTokens),
                snapshot.LastExecution.CompletedAt,
                snapshot.LastExecution.TaskComplexity,
                snapshot.LastExecution.MaxOutputTokens,
                snapshot.LastExecution.PromptCharacterCount));
        }

        if (snapshot.VerificationHistory is { Count: > 0 })
        {
            foreach (var verification in snapshot.VerificationHistory)
            {
                task.RestoreVerificationHistory(new TaskVerificationRecord(
                    verification.Command,
                    verification.WorkingDirectory,
                    verification.ExitCode,
                    verification.StandardOutput,
                    verification.StandardError,
                    verification.CompletedAt,
                    verification.ModelFitNote,
                    verification.StandardOutputPath,
                    verification.StandardErrorPath));
            }
        }

        if (snapshot.LastVerification is not null)
        {
            var latestVerification = new TaskVerificationRecord(
                snapshot.LastVerification.Command,
                snapshot.LastVerification.WorkingDirectory,
                snapshot.LastVerification.ExitCode,
                snapshot.LastVerification.StandardOutput,
                snapshot.LastVerification.StandardError,
                snapshot.LastVerification.CompletedAt,
                snapshot.LastVerification.ModelFitNote,
                snapshot.LastVerification.StandardOutputPath,
                snapshot.LastVerification.StandardErrorPath);
            if (!task._verificationHistory.Contains(latestVerification))
            {
                task.RestoreVerificationHistory(latestVerification);
            }

            task.LastVerification = latestVerification;
        }

        if (snapshot.LastDispatch is not null)
        {
            task.RecordDispatch(new TaskDispatchRecord(
                snapshot.LastDispatch.WorkerName,
                snapshot.LastDispatch.Command,
                snapshot.LastDispatch.WorkingDirectory,
                snapshot.LastDispatch.DispatchedAt,
                snapshot.LastDispatch.ProviderName,
                snapshot.LastDispatch.ModelName,
                snapshot.LastDispatch.ReasoningEffort,
                snapshot.LastDispatch.TaskComplexity,
                snapshot.LastDispatch.PromptCharacterCount,
                snapshot.LastDispatch.UsesComplexModel));
        }

        if (snapshot.LastProcess is not null)
        {
            task.RecordProcess(new TaskProcessRecord(
                snapshot.LastProcess.ProcessId,
                snapshot.LastProcess.Command,
                snapshot.LastProcess.WorkingDirectory,
                snapshot.LastProcess.StandardOutputPath,
                snapshot.LastProcess.StandardErrorPath,
                snapshot.LastProcess.ExitCodePath,
                snapshot.LastProcess.StartedAt,
                snapshot.LastProcess.CompletedAt,
                snapshot.LastProcess.ExitCode,
                snapshot.LastProcess.WasCancelled,
                snapshot.LastProcess.OwnedProcessIds));
        }

        task.SetSubscriptionRetryAfter(snapshot.SubscriptionRetryAfter);
        task.RecordSubscriptionLimitReview(
            snapshot.SubscriptionLimitReviewNote,
            snapshot.SubscriptionLimitReviewedAt,
            snapshot.SubscriptionLimitReviewedFailureCount);
        task.RestoreCriterionRetryState(snapshot.CriterionRetryCount, snapshot.CriterionRetryFeedback);
        return task;
    }

    internal void RecordExecution(TaskExecutionRecord execution) => LastExecution = execution;

    internal void ClearLastExecution() => LastExecution = null;

    internal void ClearLastDispatch() => LastDispatch = null;

    internal void ClearLastProcess() => LastProcess = null;

    internal void SetVerificationPlan(string verificationPlan) => VerificationPlan = RequireText(verificationPlan, nameof(verificationPlan));

    internal void RecordVerification(TaskVerificationRecord verification)
    {
        SubscriptionRetryAfter = null;
        if (verification.ModelFitNote is null)
        {
            var note = ModelFitEvidence.TryExtractNote(verification.StandardOutput, verification.StandardError);
            if (note is not null)
            {
                verification = verification with { ModelFitNote = note };
            }
        }

        _verificationHistory.Add(verification);
        LastVerification = verification;
    }

    internal void RestoreVerificationHistory(TaskVerificationRecord verification) => _verificationHistory.Add(verification);

    internal void ClearLatestVerification() => LastVerification = null;

    internal void SetSubscriptionRetryAfter(DateTimeOffset? retryAfter) => SubscriptionRetryAfter = retryAfter;

    internal void ClearSubscriptionRetryAfter() => SubscriptionRetryAfter = null;

    internal void RecordSubscriptionLimitReview(string? note, DateTimeOffset? reviewedAt, int failureCount)
    {
        SubscriptionLimitReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        SubscriptionLimitReviewedAt = reviewedAt;
        SubscriptionLimitReviewedFailureCount = Math.Max(0, failureCount);
    }

    internal void RecordCriterionRetryFeedback(IReadOnlyList<string> feedback)
    {
        CriterionRetryFeedback = feedback
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal void IncrementCriterionRetryCount() => CriterionRetryCount++;

    internal void ClearCriterionRetryFeedback() => CriterionRetryFeedback = [];

    private void RestoreCriterionRetryState(int retryCount, IReadOnlyList<string>? feedback)
    {
        CriterionRetryCount = Math.Max(0, retryCount);
        CriterionRetryFeedback = feedback?
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray() ?? [];
    }

    internal void RecordDispatch(TaskDispatchRecord dispatch)
    {
        SubscriptionRetryAfter = null;
        LastDispatch = dispatch;
        LastProcess = null;
    }

    internal void RecordProcess(TaskProcessRecord process) => LastProcess = process;

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        return value.Trim();
    }
}
