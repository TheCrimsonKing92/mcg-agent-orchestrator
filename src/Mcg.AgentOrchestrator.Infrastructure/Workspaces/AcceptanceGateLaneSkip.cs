namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceGateLaneSkip(string Name, string Reason)
{
    internal const string NoBuildSystemChange = "no-build-system-change";
    internal const string AbsentProject = "project-absent-from-candidate";
    internal const string OutsideDependencyClosure = "no-change-in-dependency-closure";
    internal const string ReplacedByFocusedCheck = "replaced-by-focused-check";
    internal const string SemanticDuplicate = "semantic-duplicate";
    internal const string OutsideAttributionScope = "outside-attribution-scope";
    internal const string OutsideChangeScope = "outside-change-scope";
}
