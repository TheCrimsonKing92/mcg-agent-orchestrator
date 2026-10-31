namespace Mcg.AgentOrchestrator.Core;

internal static class DeclaredTestSelectorGuidance
{
    internal static IReadOnlyList<string> For(ProjectStack? stack) => stack == ProjectStack.Declared
        ? ["- Declared-test evidence selectors: use the report's testId or name, or literal Developer-reported test names. Preserve :: paths; a module path selects its descendants. Never construct selectors by joining suite and name with a dot. Without a candidate-bound report, keep Developer names as written."]
        : [];
}
