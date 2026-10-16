namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ProgramHealthSnapshot(DateTimeOffset Now, DateTimeOffset? LastLandingAt,
    DateTimeOffset ObservedSince, string IntegrationHead, int VerifiedGoals,
    IReadOnlyList<ProgramHealthReceiptObservation> Receipts);
