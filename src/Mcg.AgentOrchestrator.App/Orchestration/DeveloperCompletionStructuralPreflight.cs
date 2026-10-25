using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeveloperCompletionStructuralPreflight
{
    internal static DeveloperCompletionStructuralFindings Evaluate(string worktreePath,
        string? projectHomeDirectory = null, string? integrationBranch = null) =>
        Evaluate(worktreePath, projectHomeDirectory, readBase: integrationBranch is null ? null
            : path => LaneSubstringCollisionBase.Read(path, integrationBranch));

    internal static DeveloperCompletionStructuralFindings Evaluate(string worktreePath,
        string? projectHomeDirectory, Func<string, LaneSubstringCollisionBase?>? readBase)
    {
        var ratchet = SourceSizeRatchetPreflight.Evaluate(worktreePath);
        if (ratchet.HasBlockingViolation)
        {
            return new DeveloperCompletionStructuralFindings(true, ratchet.Message);
        }

        AcceptanceGateEngineSettings settings;
        try
        {
            settings = AcceptanceGateEngineSettings.Load(worktreePath, projectHomeDirectory);
        }
        catch (InvalidDataException exception)
        {
            return new DeveloperCompletionStructuralFindings(
                true,
                $"{AcceptanceManifestDisplayPathResolver.Resolve(worktreePath, projectHomeDirectory)}: {exception.Message}");
        }

        try
        {
            var violations = EvaluateTestCollectionGuards(worktreePath, settings.InfrastructureTestLanes,
                projectHomeDirectory, readBase);
            if (violations is not null)
                return new DeveloperCompletionStructuralFindings(true, violations);
        }
        catch (InvalidDataException exception)
        {
            return new DeveloperCompletionStructuralFindings(true, $"test collection guard violations: {exception.Message}");
        }

        return new DeveloperCompletionStructuralFindings(
            false,
            "source size ratchet and acceptance manifest lane consistency checks passed");
    }

    private static string? EvaluateTestCollectionGuards(string worktreePath, IReadOnlyList<AcceptanceTestLane> lanes,
        string? projectHomeDirectory, Func<string, LaneSubstringCollisionBase?>? readBase)
    {
        if (!Directory.Exists(Path.Combine(worktreePath, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests")) ||
            !lanes.Any(lane => lane.Name == TestCollectionGuardRules.DotnetBuildSlotsLaneName))
            return null;

        var violations = new List<TestCollectionGuardViolation>();
        IReadOnlyDictionary<string, string>? exemptions = null;
        try
        {
            exemptions = TestCollectionGuardRules.LoadIsolatedCollectionExemptions(worktreePath);
        }
        catch (InvalidDataException exception)
        {
            violations.Add(new TestCollectionGuardViolation(TestCollectionGuardRules.IsolatedCollectionRule,
                "", null, [], [], exception.Message));
        }

        var descriptors = AcceptanceTestClassSourceScanner.Scan(worktreePath);
        violations.AddRange(TestCollectionGuardRules.FindLaneViolations(lanes, descriptors));
        if (readBase is not null && string.Equals(
                Path.GetFullPath(AcceptanceManifestLocator.Resolve(worktreePath, projectHomeDirectory)),
                Path.GetFullPath(Path.Combine(worktreePath, "config", "acceptance-manifest.json")),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            readBase(worktreePath) is { } baseline)
        {
            AcceptanceGateEngineSettings? baseSettings = null;
            try
            {
                baseSettings = AcceptanceGateEngineSettings.Parse(baseline.BaseManifestJson);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                // Unavailable base evidence must not suppress the existing structural guards.
            }
            if (baseSettings is not null)
                violations.AddRange(LaneSubstringCollisionCheck.Find(baseSettings.InfrastructureTestLanes,
                    lanes, descriptors, baseline.DiffText));
        }
        if (exemptions is not null)
            violations.AddRange(TestCollectionGuardRules.FindUnisolatedLaneClasses(lanes, descriptors,
                TestCollectionGuardRules.DotnetBuildSlotsLaneName, TestCollectionGuardRules.DotnetBuildSlotsCollection,
                exemptions.Keys));
        if (violations.Count == 0) return null;

        // ScanSources reparses the source tree; only pay that cost when feedback needs file paths.
        var sources = violations.Any(item => item.ClassFullName.Length > 0)
            ? AcceptanceTestClassSourceScanner.ScanSources(worktreePath, includeAncestorSources: false)
                .ToDictionary(item => item.FullName, StringComparer.Ordinal)
            : null;
        var entries = violations.Select(item => new
        {
            Violation = item,
            Path = item.ClassFullName.Length == 0 ? TestCollectionGuardRules.ExemptionsRelativePath
                : sources![item.ClassFullName].SourcePaths.First()
        }).OrderBy(item => item.Violation.Rule, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Violation.ClassFullName, StringComparer.Ordinal);
        return "test collection guard violations: " + string.Join("; ", entries.Select(item =>
            $"{item.Path}: {item.Violation.ClassFullName} [{item.Violation.Rule}] {item.Violation.RepairHint}"));
    }
}

internal sealed record DeveloperCompletionStructuralFindings(bool HasViolation, string Message);
