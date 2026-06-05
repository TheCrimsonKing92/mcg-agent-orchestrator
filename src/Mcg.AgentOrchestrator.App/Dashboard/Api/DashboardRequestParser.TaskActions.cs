using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardRequestParser
{
public static DispatchSubmissionDto ParseDispatchSubmission(string body)
{
    var submission = JsonSerializer.Deserialize<DispatchSubmissionDto>(body, DashboardJson.Options());
    if (string.IsNullOrWhiteSpace(submission?.WorkerName))
    {
        throw new ArgumentException("Dispatch JSON must include a non-empty 'workerName' value.");
    }

    if (string.IsNullOrWhiteSpace(submission.Command))
    {
        throw new ArgumentException("Dispatch JSON must include a non-empty 'command' value.");
    }

    return new DispatchSubmissionDto(submission.WorkerName.Trim(), submission.Command.Trim());
}

public static VerifySubmissionDto ParseVerifySubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        throw new ArgumentException("Verification command cannot be empty.");
    }

    var trimmed = body.Trim();
    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        return new VerifySubmissionDto(trimmed);
    }

    var submission = JsonSerializer.Deserialize<VerifySubmissionDto>(trimmed, DashboardJson.Options());
    if (string.IsNullOrWhiteSpace(submission?.Command))
    {
        throw new ArgumentException("Verification JSON must include a non-empty 'command' value.");
    }

    return new VerifySubmissionDto(submission.Command.Trim());
}

public static ManualVerifySubmissionDto ParseManualVerifySubmission(string body)
{
    var submission = JsonSerializer.Deserialize<ManualVerifySubmissionDto>(body, DashboardJson.Options());
    if (submission is null)
    {
        throw new ArgumentException("Manual verification JSON body was invalid.");
    }

    if (string.IsNullOrWhiteSpace(submission.Note))
    {
        throw new ArgumentException("Manual verification JSON must include a non-empty 'note' value.");
    }

    return submission with { Note = submission.Note.Trim() };
}

public static ProgressSubmissionDto ParseProgressSubmission(string body)
{
    var submission = JsonSerializer.Deserialize<ProgressSubmissionDto>(body, DashboardJson.Options());
    if (submission is null)
    {
        throw new ArgumentException("Progress JSON body was invalid.");
    }

    if (string.IsNullOrWhiteSpace(submission.Status))
    {
        throw new ArgumentException("Progress JSON must include a non-empty 'status' value.");
    }

    if (string.IsNullOrWhiteSpace(submission.Message))
    {
        throw new ArgumentException("Progress JSON must include a non-empty 'message' value.");
    }

    return new ProgressSubmissionDto(submission.Status.Trim(), submission.Message.Trim());
}

public static RetrySubmissionDto ParseRetrySubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        return new RetrySubmissionDto("Retry requested.");
    }

    var trimmed = body.Trim();
    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        return new RetrySubmissionDto(trimmed);
    }

    var submission = JsonSerializer.Deserialize<RetrySubmissionDto>(trimmed, DashboardJson.Options());
    return new RetrySubmissionDto(string.IsNullOrWhiteSpace(submission?.Message) ? "Retry requested." : submission.Message.Trim());
}

public static VerificationPlanSubmissionDto ParseVerificationPlanSubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        throw new ArgumentException("Verification plan cannot be empty.");
    }

    var trimmed = body.Trim();
    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        return new VerificationPlanSubmissionDto(trimmed);
    }

    var submission = JsonSerializer.Deserialize<VerificationPlanSubmissionDto>(trimmed, DashboardJson.Options());
    if (string.IsNullOrWhiteSpace(submission?.Plan))
    {
        throw new ArgumentException("Verification plan JSON must include a non-empty 'plan' value.");
    }

    return new VerificationPlanSubmissionDto(submission.Plan.Trim());
}

public static AskSubmissionDto ParseAskSubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        throw new ArgumentException("Question cannot be empty.");
    }

    var trimmed = body.Trim();
    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        return new AskSubmissionDto(trimmed);
    }

    var submission = JsonSerializer.Deserialize<AskSubmissionDto>(trimmed, DashboardJson.Options());
    if (string.IsNullOrWhiteSpace(submission?.Question))
    {
        throw new ArgumentException("Question JSON must include a non-empty 'question' value.");
    }

    return new AskSubmissionDto(submission.Question.Trim());
}

public static ProfileDispatchReadySubmissionDto ParseProfileDispatchReadySubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        throw new ArgumentException("Profile dispatch body cannot be empty.");
    }

    var trimmed = body.Trim();
    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        return new ProfileDispatchReadySubmissionDto(trimmed);
    }

    var submission = JsonSerializer.Deserialize<ProfileDispatchReadySubmissionDto>(trimmed, DashboardJson.Options());
    if (string.IsNullOrWhiteSpace(submission?.ProfileName))
    {
        throw new ArgumentException("Profile dispatch JSON must include a non-empty 'profileName' value.");
    }

    return new ProfileDispatchReadySubmissionDto(submission.ProfileName.Trim());
}
}
