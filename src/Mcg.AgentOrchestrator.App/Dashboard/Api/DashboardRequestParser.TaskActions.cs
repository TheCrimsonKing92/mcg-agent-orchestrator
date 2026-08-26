using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

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
    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
    {
        throw new ArgumentException("Manual verification JSON body was invalid.");
    }

    var hasPassed = TryGetProperty(root, "passed", out var passedElement);
    var hasStatus = TryGetProperty(root, "status", out var statusElement);
    if (!TryGetProperty(root, "note", out var noteElement) ||
        noteElement.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(noteElement.GetString()))
    {
        throw new ArgumentException("Manual verification JSON must include a non-empty 'note' value.");
    }

    bool? passed = null;
    if (hasPassed)
    {
        if (passedElement.ValueKind != JsonValueKind.True && passedElement.ValueKind != JsonValueKind.False)
        {
            throw new ArgumentException("Manual verification JSON 'passed' must be true or false.");
        }

        passed = passedElement.GetBoolean();
    }

    if (hasStatus)
    {
        var status = statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString()?.Trim()
            : null;
        bool? statusPassed = status?.ToLowerInvariant() switch
        {
            "passed" or "pass" or "true" => true,
            "failed" or "fail" or "false" => false,
            _ => null
        };
        if (statusPassed is null)
        {
            throw new ArgumentException("Manual verification JSON 'status' must be passed or failed.");
        }

        if (passed is not null && passed.Value != statusPassed.Value)
        {
            throw new ArgumentException("Manual verification JSON 'passed' and 'status' values conflict.");
        }

        passed = statusPassed;
    }

    if (passed is null)
    {
        throw new ArgumentException("Manual verification JSON must include 'passed' or 'status'.");
    }

    var idempotencyKey = TryGetProperty(root, "idempotencyKey", out var idempotencyElement) &&
        idempotencyElement.ValueKind == JsonValueKind.String
        ? idempotencyElement.GetString()?.Trim()
        : null;
    return new ManualVerifySubmissionDto(passed.Value, noteElement.GetString()!.Trim(), idempotencyKey);
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
        throw new ArgumentException("Retry note cannot be empty.");
    }

    var trimmed = body.Trim();
    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        throw new ArgumentException("Retry submission must be JSON and include an explicit 'cause' value.");

    var submission = JsonSerializer.Deserialize<RetrySubmissionDto>(trimmed, DashboardJson.Options());
    if (string.IsNullOrWhiteSpace(submission?.Message))
    {
        throw new ArgumentException("Retry JSON must include a non-empty 'message' value.");
    }
    if (string.IsNullOrWhiteSpace(submission.Cause) ||
        !Enum.TryParse<RetryCause>(submission.Cause, ignoreCase: true, out var cause) ||
        !Enum.IsDefined(cause) ||
        cause == RetryCause.Unknown)
    {
        throw new ArgumentException("Retry JSON must include a supported non-Unknown 'cause' value.");
    }

    return new RetrySubmissionDto(
        submission.Message.Trim(),
        cause.ToString(),
        submission.Mechanical,
        submission.IdempotencyKey?.Trim());
}

public static LimitReviewSubmissionDto? ParseLimitReviewSubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        return null;
    }

    var trimmed = body.Trim();
    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        return new LimitReviewSubmissionDto(true, trimmed);
    }

    using var document = JsonDocument.Parse(trimmed);
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
    {
        throw new ArgumentException("Subscription limit review JSON body was invalid.");
    }

    if (!TryGetProperty(root, "confirmLimitReview", out var confirmElement) ||
        confirmElement.ValueKind is not JsonValueKind.True)
    {
        throw new ArgumentException("Subscription limit review JSON must include confirmLimitReview=true.");
    }

    if (!TryGetProperty(root, "note", out var noteElement) ||
        noteElement.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(noteElement.GetString()))
    {
        throw new ArgumentException("Subscription limit review JSON must include a non-empty 'note' value.");
    }

    return new LimitReviewSubmissionDto(true, noteElement.GetString()!.Trim());
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

private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
{
    foreach (var property in element.EnumerateObject())
    {
        if (property.NameEquals(name) ||
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            value = property.Value;
            return true;
        }
    }

    value = default;
    return false;
}
}
