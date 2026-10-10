namespace Mcg.AgentOrchestrator.Core;

internal static class ForeignTreeNotice
{
    public static string Describe(IReadOnlyList<string> removedPaths) =>
        string.Join("; ", removedPaths
            .Select(path => $"skipped removed path: {path} (absent from candidate tree)")
            .Append("candidate tree contains none of this tool's built-in test projects; " +
                "the checks to run are the ones the project's acceptance manifest declares"));
}
