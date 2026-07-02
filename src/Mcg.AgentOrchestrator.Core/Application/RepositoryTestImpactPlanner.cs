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

    private const string CliInfrastructureFilter =
        "FullyQualifiedName~CliHelpTests";

    private const string DashboardInfrastructureFilter =
        "FullyQualifiedName~DashboardRenderingTests|FullyQualifiedName~DashboardValidationHarnessTests";

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
        var coreTestFilter = BuildChangedTestClassFilter(summary, "tests/Mcg.AgentOrchestrator.Core.Tests/");
        var infrastructureTestFilter = BuildChangedTestClassFilter(summary, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/");
        var appSubsystems = summary.Files
            .Select(file => AppSubsystem(file.Path))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var touchesApp = appSubsystems.Length > 0;
        var touchesScriptsOrConfig = summary.Files.Any(file =>
            file.Categories.Contains(RepositoryChangeCategory.Script) ||
            file.Categories.Contains(RepositoryChangeCategory.Configuration));
        var focusedInfrastructureFilter = TryBuildFocusedInfrastructureFilter(summary, appSubsystems);

        if (touchesCore)
        {
            if (coreTestFilter is not null &&
                !summary.Files.Any(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Core/")))
            {
                checks.Add(new RepositoryTestImpactCheck(
                    "focused changed core tests",
                    [.. CoreTests, "--filter", coreTestFilter],
                    "Only Core test files changed; run the touched test classes."));
            }
            else
            {
                checks.Add(new RepositoryTestImpactCheck(
                    "core tests",
                    CoreTests,
                    "Core contracts changed or core tests changed."));
            }
        }

        if (focusedInfrastructureFilter is not null)
        {
            var filter = JoinFilters(focusedInfrastructureFilter.Value.Filter, infrastructureTestFilter);
            checks.Add(new RepositoryTestImpactCheck(
                focusedInfrastructureFilter.Value.Name,
                [.. InfrastructureTests, "--filter", filter],
                focusedInfrastructureFilter.Value.Reason));
        }
        else if (infrastructureTestFilter is not null &&
            !summary.Files.Any(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure/")) &&
            !touchesApp &&
            !touchesScriptsOrConfig)
        {
            checks.Add(new RepositoryTestImpactCheck(
                "focused changed infrastructure tests",
                [.. InfrastructureTests, "--filter", infrastructureTestFilter],
                "Only Infrastructure test files changed; run the touched test classes."));
        }
        else if (touchesInfrastructure || touchesApp || touchesScriptsOrConfig)
        {
            checks.Add(new RepositoryTestImpactCheck(
                "infrastructure tests",
                InfrastructureTests,
                touchesApp && appSubsystems.Length > 1
                    ? "Multiple App subsystems changed; run the full Infrastructure test suite."
                    : touchesApp
                    ? "App behavior lacks a focused test-impact mapping; run the full Infrastructure test suite."
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
            // Reaching here means narrow per-project checks were selected, so the TEST scope is not
            // broad even when the change is escalation-broad (core/infra). Escalation/review breadth
            // stays on summary.RequiresBroadVerification so core/infra changes still escalate.
            RequiresBroadVerification: false,
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

    private static string? BuildChangedTestClassFilter(RepositoryChangeSummary summary, string testProjectPrefix)
    {
        var classFilters = summary.Files
            .Where(file => StartsWith(file.Path, testProjectPrefix))
            .Select(file => Path.GetFileNameWithoutExtension(file.Path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => $"FullyQualifiedName~{name}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return classFilters.Length == 0 ? null : string.Join("|", classFilters);
    }

    private static string JoinFilters(string left, string? right) =>
        string.IsNullOrWhiteSpace(right) ? left : $"{left}|{right}";

    private static (string Name, string Filter, string Reason)? TryBuildFocusedInfrastructureFilter(
        RepositoryChangeSummary summary,
        string[] appSubsystems)
    {
        if (appSubsystems.Length != 1)
            return null;

        if (summary.Files.Any(file =>
            StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure/") ||
            file.Categories.Contains(RepositoryChangeCategory.Script) ||
            file.Categories.Contains(RepositoryChangeCategory.Configuration)))
            return null;

        if (summary.Files.Count(file => file.Categories.Contains(RepositoryChangeCategory.Source)) > 5)
            return null;

        var subsystem = appSubsystems[0];
        if (subsystem.Equals("cli", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "focused CLI infrastructure tests",
                CliInfrastructureFilter,
                "CLI-only App change; run the CLI-related Infrastructure test classes.");
        }

        if (subsystem.Equals("dashboard", StringComparison.OrdinalIgnoreCase) ||
            subsystem.Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "focused dashboard infrastructure tests",
                DashboardInfrastructureFilter,
                "Dashboard/API-only App change; run dashboard rendering and validation Infrastructure tests.");
        }

        return null;
    }

    private static string? AppSubsystem(string path)
    {
        if (StartsWith(path, "src/Mcg.AgentOrchestrator.App/Cli/"))
            return "cli";

        if (StartsWith(path, "src/Mcg.AgentOrchestrator.App/Dashboard/Api/"))
            return "api";

        if (StartsWith(path, "src/Mcg.AgentOrchestrator.App/Dashboard/"))
            return "dashboard";

        return null;
    }
}
