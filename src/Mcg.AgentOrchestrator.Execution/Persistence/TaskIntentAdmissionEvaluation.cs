namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TaskIntentAdmissionEvaluation(
    TaskIntentSuppressionReceipt? Suppression,
    TaskIntentSuppressionReceipt? Pending,
    IReadOnlyList<TaskIntentAdmissionEvent> Rejected);
