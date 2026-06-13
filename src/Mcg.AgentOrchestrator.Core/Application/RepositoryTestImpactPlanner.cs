namespace Mcg.AgentOrchestrator.Core;

public sealed record RepositoryTestImpactCheck(
    string Name,
    IReadOnlyList<string> Command,
    string Reason)
{
    public string CommandLine => Command.Count == 0
        ? "(no command)"
        : string.Join(" ", Command);
}

public sealed record RepositoryTestImpactPlan(
    bool RequiresBuild,
    bool RequiresBroadVerification,
    string Summary,
    IReadOnlyList<RepositoryTestImpactCheck> Checks);

public static class RepositoryTestImpactPlanner
{
    private static readonly string[] CoreTests =
    [
        "dotnet",
        "test",
        "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
        "--verbosity",
        "minimal"
    ];

    private static readonly string[] InfrastructureTests =
    [
        "dotnet",
        "test",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        "--verbosity",
        "minimal"
    ];

    private static readonly string[] FullDotnetTests =
    [
        "dotnet",
        "test",
        "--verbosity",
        "minimal"
    ];

    public static RepositoryTestImpactPlan Plan(IEnumerable<string> paths) =>
        Plan(RepositoryChangeClassifier.Classify(paths));

    public static RepositoryTestImpactPlan Plan(RepositoryChangeSummary summary)
    {
        if (summary.Files.Count == 0)
        {
            return NoBuild("No changed files detected; inspect the branch diff before running tests.");
        }

        if (summary.HasGeneratedArtifacts)
        {
            return NoBuild("Generated artifacts are present; remove them before selecting verification commands.");
        }

        if (summary.IsDocsOnly)
        {
            return NoBuild("Documentation-only change; no build or test command is required.");
        }

        if (summary.HasBuildSystemChanges)
        {
            return FullSuite("Build configuration changed; run the full relevant dotnet suite.");
        }

        if (summary.HasSecuritySensitiveChanges)
        {
            return FullSuite("Security, sandbox, credential, or policy-sensitive paths changed; run broad tests and manual review.");
        }

        var checks = new List<RepositoryTestImpactCheck>();
        var touchesCore = summary.Files.Any(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Core/") ||
            StartsWith(file.Path, "tests/Mcg.AgentOrchestrator.Core.Tests/"));
        var touchesInfrastructure = summary.Files.Any(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure/") ||
            StartsWith(file.Path, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/"));
        var touchesApp = summary.Files.Any(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.App/"));
        var touchesScriptsOrConfig = summary.Files.Any(file =>
            file.Categories.Contains(RepositoryChangeCategory.Script) ||
            file.Categories.Contains(RepositoryChangeCategory.Configuration));

        if (touchesCore)
        {
            checks.Add(new RepositoryTestImpactCheck(
                "core tests",
                CoreTests,
                "Core contracts changed or core tests changed."));
            checks.Add(new RepositoryTestImpactCheck(
                "infrastructure tests",
                InfrastructureTests,
                "Core behavior feeds infrastructure and CLI/dashboard integration."));
        }

        if (touchesInfrastructure || touchesApp || touchesScriptsOrConfig)
        {
            checks.Add(new RepositoryTestImpactCheck(
                "infrastructure tests",
                InfrastructureTests,
                touchesApp
                    ? "App, CLI, or dashboard behavior is covered by the infrastructure test suite."
                    : "Infrastructure, script, or configuration behavior changed."));
        }

        var distinctChecks = checks
            .GroupBy(check => check.CommandLine, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (distinctChecks.Length == 0)
        {
            return FullSuite("Changed files do not map to a narrower test target; run the full relevant dotnet suite.");
        }

        return new RepositoryTestImpactPlan(
            RequiresBuild: true,
            RequiresBroadVerification: touchesCore || summary.RequiresBroadVerification,
            Summary: distinctChecks.Length == 1
                ? $"Selected {distinctChecks[0].Name} from changed file scope."
                : $"Selected {distinctChecks.Length} test commands from changed file scope.",
            Checks: distinctChecks);
    }

    private static RepositoryTestImpactPlan NoBuild(string summary) =>
        new(
            RequiresBuild: false,
            RequiresBroadVerification: false,
            Summary: summary,
            Checks:
            [
                new RepositoryTestImpactCheck(
                    "test impact: no build required",
                    [],
                    summary)
            ]);

    private static RepositoryTestImpactPlan FullSuite(string summary) =>
        new(
            RequiresBuild: true,
            RequiresBroadVerification: true,
            Summary: summary,
            Checks:
            [
                new RepositoryTestImpactCheck(
                    "full dotnet tests",
                    FullDotnetTests,
                    summary)
            ]);

    private static bool StartsWith(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
