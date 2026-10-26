namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record GoalFinalizedLanding(
    string ReceiptKind, string ReceiptId, string CandidateRevision, string LandingCommit);
