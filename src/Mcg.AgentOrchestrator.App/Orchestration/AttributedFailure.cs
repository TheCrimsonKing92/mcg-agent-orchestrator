namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AttributedFailure(
    string Source, string ReceiptId, string CandidateSha, IReadOnlyList<string> FailingTests);
