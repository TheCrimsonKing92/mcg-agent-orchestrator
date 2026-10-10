namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ManifestCheckComparison(string ProjectPath, ManifestCheckResultKind Kind,
    string? LearnedRunner, string? ManifestRunner, string Reason);
