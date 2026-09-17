using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A CLI request has no caller-supplied observation time. Its first durable submission
// supplies that time; retrying transport must not manufacture a different observation.
public sealed record ManualVerificationRequest(bool Passed, string Note, string WorkingDirectory);

public sealed record ManualVerificationOperatorIntentPayload(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TaskVerificationRecord? Verification = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ManualVerificationRequest? Request = null)
{
    public TaskVerificationRecord ResolveVerification(DateTimeOffset submittedAt) => (Verification, Request) switch
    {
        ({ } verification, null) => verification,
        (null, { } request) => ManualVerificationRecorder.Create(request.Passed, request.Note, request.WorkingDirectory, submittedAt),
        _ => throw new InvalidOperationException("Manual verification requires exactly one explicit receipt or verification request.")
    };
}
