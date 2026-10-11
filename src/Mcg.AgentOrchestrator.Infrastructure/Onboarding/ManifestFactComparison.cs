namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ManifestFactComparison(
    string Fact,
    ManifestFactResultKind Kind,
    string Hand,
    string Learned,
    string Reason);
