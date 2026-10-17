using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreTesterTestSourceGuardClasses
{
    internal sealed record Entry(string TestClass, string Reason);

    internal static IReadOnlyList<Entry> SharedTableClasses { get; } = Array.AsReadOnly<Entry>(
    [
        new("CliCommandCapabilitiesTestsQueryRouteConvention", "Owns the served-forms table shared by CLI source checks."),
        new("CliServedFormsSqliteStoreEffectsTests", "Reads the served-forms table by reflection to check SQLite effects."),
        new("CliCommandCapabilitiesTestsQueryRouteNoWriterEffects", "Reads the served-forms table by reflection to check writer effects.")
    ]);

    internal static IReadOnlyList<FindingEvidenceSelection> Select(
        string worktreePath, IReadOnlyList<string> changedPaths)
    {
        if (!changedPaths.Any(path => path.Replace('\\', '/').TrimStart('.', '/')
                .StartsWith("tests/", StringComparison.OrdinalIgnoreCase))) return [];
        var root = Path.Combine(worktreePath, "tests");
        if (!Directory.Exists(root)) return [];

        var names = DeveloperDeferredTestSelections.EnumerateSourceFiles(root)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name!.EndsWith("GuardTests", StringComparison.Ordinal))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Concat(SharedTableClasses.Select(entry => entry.TestClass))
            .ToArray();
        return DeveloperDeferredTestSelections.ResolveNames(
            worktreePath, names, requireUniqueSourceFile: true).Selections;
    }

    internal static bool ContainsClass(string? testClass) =>
        testClass is not null && (testClass.EndsWith("GuardTests", StringComparison.Ordinal) ||
            SharedTableClasses.Any(entry => string.Equals(entry.TestClass, testClass, StringComparison.Ordinal)));

    internal static bool IsCovered(string testIdentity) =>
        ContainsClass(AcceptanceTestSourceResolver.ExtractClassName(testIdentity));
}
