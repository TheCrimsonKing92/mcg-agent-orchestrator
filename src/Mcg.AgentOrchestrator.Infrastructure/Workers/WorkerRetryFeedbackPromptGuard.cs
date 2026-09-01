using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerRetryFeedbackPromptGuard
{
    internal const string ErrorCode = "retry-feedback-not-delivered";
    private const string LogicalIdentity = "task/criterion-retry-feedback.json";

    internal static WorkerContextPackageReceipt? Validate(
        Goal goal,
        TaskSpec task,
        WorkerContextPackage? package,
        WorkerContextPackageReceipt? receipt,
        string promptPath)
    {
        if (package is null)
        {
            return receipt;
        }

        var acceptedRetry = task.AcceptedRetryFeedback;
        if (acceptedRetry is null)
        {
            return receipt;
        }

        var expectedFeedback = new[] { acceptedRetry.Message };
        if (!task.CriterionRetryFeedback.SequenceEqual(expectedFeedback, StringComparer.Ordinal))
        {
            throw Failure(task, "authoritative current-round feedback does not match the newest accepted retry note");
        }

        var artifact = package.Artifacts.SingleOrDefault(item =>
            item.Identity.Value.Equals(LogicalIdentity, StringComparison.Ordinal));
        if (artifact is null || artifact.DeliveryMode != ContextDeliveryMode.InlineFull || artifact.AuthoritativeBytes is null)
        {
            throw Failure(task, "typed retry-feedback artifact is absent or not inline");
        }

        var typedFeedback = JsonSerializer.Deserialize<string[]>(artifact.AuthoritativeBytes);
        if (typedFeedback is null || !typedFeedback.SequenceEqual(expectedFeedback, StringComparer.Ordinal))
        {
            throw Failure(task, "typed retry-feedback bytes do not contain the newest accepted retry note");
        }

        var projection = WorkerContextPackageBuilder.RenderArtifact(artifact);
        var promptBytes = File.ReadAllBytes(promptPath);
        var promptText = Encoding.UTF8.GetString(promptBytes);
        if (!promptText.Contains(projection, StringComparison.Ordinal))
        {
            throw Failure(task, "the generated prompt does not contain the exact typed retry-feedback projection");
        }

        return (receipt ?? throw Failure(task, "context-package receipt is absent")) with
        {
            RetryFeedbackPromptReceipt = new WorkerRetryFeedbackPromptReceipt(
                LogicalIdentity,
                task.Id.Value,
                acceptedRetry.AcceptedAt,
                HashNormalized(acceptedRetry.Message),
                artifact.ContentHash,
                WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(projection)),
                WorkerContextArtifact.Hash(promptBytes),
                artifact.DeliveryMode)
        };
    }

    private static WorkerSubscriptionPreflightException Failure(TaskSpec task, string detail) => new(
        $"Worker dispatch blocked: {ErrorCode}: {detail} for task {task.Id.Value}.",
        ErrorCode,
        [$"blocked: {ErrorCode}: {detail} for task {task.Id.Value}"]);

    private static string HashNormalized(string value) => WorkerContextArtifact.Hash(
        Encoding.UTF8.GetBytes(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim()));
}
