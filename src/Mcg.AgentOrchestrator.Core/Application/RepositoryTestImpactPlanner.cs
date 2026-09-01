namespace Mcg.AgentOrchestrator.Core;

public sealed record RepositoryTestImpactCheck(
    string Name,
    IReadOnlyList<string> Command,
    string Reason,
    RepositoryTestProject? TestProject = null,
    IReadOnlyList<string>? TestClassSelections = null)
{
    public string CommandLine => Command.Count == 0
        ? "(no command)"
        : string.Join(" ", Command);
}

public enum RepositoryTestProject
{
    Core,
    Infrastructure,
    ProviderEnvironment,
    Cli,
    Dashboard
}

public sealed record RepositoryTestImpactPlan(
    bool RequiresBuild,
    bool RequiresBroadVerification,
    string Summary,
    IReadOnlyList<RepositoryTestImpactCheck> Checks);

public static class RepositoryTestImpactPlanner
{
    private sealed record TestClassFilterBuildResult(
        string? Filter,
        string? AbandonReason,
        IReadOnlyList<string> TestClasses);

    private static readonly string[] CoreTests =
    [
        "dotnet",
        "test",
        "--project",
        "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
        "--verbosity",
        "minimal"
    ];

    private static readonly string[] InfrastructureTests =
    [
        "dotnet",
        "test",
        "--project",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        "--verbosity",
        "minimal"
    ];

    private static readonly string[] ProviderEnvironmentTests =
    [
        "dotnet",
        "test",
        "--project",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj",
        "--verbosity",
        "minimal"
    ];

    private static readonly string[] CliTests =
    [
        "dotnet",
        "test",
        "--project",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj",
        "--verbosity",
        "minimal"
    ];

    private static readonly string[] DashboardTests =
    [
        "dotnet",
        "test",
        "--project",
        "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj",
        "--verbosity",
        "minimal"
    ];

    private static readonly string[] CliInfrastructureClasses =
        ["CliCommandTests", "CliHelpTests"];

    private static readonly string CliInfrastructureFilter = BuildClassFilter(CliInfrastructureClasses);

    private static readonly string[] DashboardClasses =
    [
        "DashboardRenderingTests",
        "DashboardHostTests",
        "DashboardDispatchStartFailureEndpointTests",
        "DashboardValidationHarnessTests"
    ];

    private static readonly string DashboardFilter =
        $"({BuildClassFilter(DashboardClasses)})&Category!=HostIntegration";

    // The full suite is expressed as per-project runs rather than one solution-level
    // "dotnet test": the test projects are Microsoft.Testing.Platform, and a project-less
    // dotnet-test check cannot be routed to the MTP runner, so it always dies on .NET 10 with
    // "Testing with VSTest target is no longer supported".


    public static RepositoryTestImpactPlan Plan(IEnumerable<string> paths) =>
        Plan(RepositoryChangeClassifier.Classify(paths));

    public static RepositoryTestImpactPlan Plan(IEnumerable<string> paths, string repositoryRoot) =>
        Plan(RepositoryChangeClassifier.Classify(paths), repositoryRoot);

    public static RepositoryTestImpactPlan Plan(RepositoryChangeSummary summary) =>
        Plan(summary, FileSystemTestClassDeclarationReader.CreateForCurrentRepository());

    public static RepositoryTestImpactPlan Plan(
        RepositoryChangeSummary summary,
        string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (!Path.IsPathFullyQualified(repositoryRoot))
        {
            throw new ArgumentException(
                "The repository root must be an absolute path.",
                nameof(repositoryRoot));
        }

        return Plan(summary, new FileSystemTestClassDeclarationReader(repositoryRoot));
    }

    internal static RepositoryTestImpactPlan Plan(
        RepositoryChangeSummary summary,
        ITestClassDeclarationReader declarationReader)
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
        var touchesInfrastructure = summary.Files.Any(file =>
            StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure/") ||
            StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure.Providers/") ||
            StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/") ||
            StartsWith(file.Path, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/"));
        var touchesDashboardTests = summary.Files.Any(file =>
            StartsWith(file.Path, "tests/Mcg.AgentOrchestrator.Dashboard.Tests/"));
        var coreTestFilter = BuildChangedTestClassFilter(
            summary,
            "tests/Mcg.AgentOrchestrator.Core.Tests/",
            declarationReader);
        var infrastructureTestFilter = BuildChangedTestClassFilter(
            summary,
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/",
            declarationReader);
        var dashboardTestFilter = BuildChangedTestClassFilter(
            summary,
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/",
            declarationReader);
        var appSubsystems = summary.Files
            .Select(file => AppSubsystem(file.Path))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var touchesApp = appSubsystems.Length > 0;
        var touchesDashboardApp = appSubsystems.Any(IsDashboardSubsystem);
        var touchesDashboard = touchesDashboardApp || touchesDashboardTests;
        var touchesNonDashboardApp = appSubsystems.Any(subsystem => !IsDashboardSubsystem(subsystem));
        var touchesScriptsOrConfig = summary.Files.Any(file =>
            file.Categories.Contains(RepositoryChangeCategory.Script) ||
            file.Categories.Contains(RepositoryChangeCategory.Configuration));
        var focusedInfrastructureFilter = TryBuildFocusedInfrastructureFilter(
            summary,
            appSubsystems,
            declarationReader);

        if (touchesCore)
        {
            if (coreTestFilter.Filter is not null &&
                !summary.Files.Any(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Core/")))
            {
                checks.Add(new RepositoryTestImpactCheck(
                    "focused changed core tests",
                    [.. CoreTests, "--filter", coreTestFilter.Filter],
                    "Only Core test files changed; run the touched test classes.",
                    RepositoryTestProject.Core,
                    coreTestFilter.TestClasses));
            }
            else
            {
                checks.Add(new RepositoryTestImpactCheck(
                    "core tests",
                    CoreTests,
                    coreTestFilter.AbandonReason ?? "Core contracts changed or core tests changed.",
                    RepositoryTestProject.Core));
            }
        }

        if (touchesDashboard)
        {
            var mappedDashboardFilter = touchesDashboardApp
                ? JoinFilters(DashboardFilter, dashboardTestFilter.Filter)
                : dashboardTestFilter.Filter;
            var mappedDashboardClasses = touchesDashboardApp
                ? DashboardClasses.Concat(dashboardTestFilter.TestClasses).Distinct(StringComparer.Ordinal).ToArray()
                : dashboardTestFilter.TestClasses;
            var useFocusedDashboardFilter =
                mappedDashboardFilter is not null &&
                dashboardTestFilter.AbandonReason is null &&
                CanUseFocusedAppFilters(summary);
            checks.Add(new RepositoryTestImpactCheck(
                useFocusedDashboardFilter ? "focused dashboard infrastructure tests" : "dashboard tests",
                useFocusedDashboardFilter
                    ? [.. DashboardTests, "--filter", mappedDashboardFilter!]
                    : DashboardTests,
                useFocusedDashboardFilter
                    ? "Dashboard or API behavior changed; run the mapped Dashboard test classes."
                    : dashboardTestFilter.AbandonReason ??
                        "Dashboard behavior changed alongside shared behavior; run the full Dashboard test suite.",
                RepositoryTestProject.Dashboard,
                useFocusedDashboardFilter ? mappedDashboardClasses : null));
        }

        if (focusedInfrastructureFilter is not null && infrastructureTestFilter.AbandonReason is null)
        {
            var filter = JoinFilters(focusedInfrastructureFilter.Value.Filter, infrastructureTestFilter.Filter);
            var selectedClasses = focusedInfrastructureFilter.Value.TestClasses
                .Concat(infrastructureTestFilter.TestClasses)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            checks.Add(new RepositoryTestImpactCheck(
                focusedInfrastructureFilter.Value.Name,
                [.. InfrastructureTests, "--filter", filter],
                focusedInfrastructureFilter.Value.Reason,
                RepositoryTestProject.Infrastructure,
                selectedClasses));
        }
        else if (infrastructureTestFilter.Filter is not null &&
            !summary.Files.Any(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure/")) &&
            !summary.Files.Any(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure.Providers/")) &&
            !touchesApp &&
            !touchesScriptsOrConfig)
        {
            checks.Add(new RepositoryTestImpactCheck(
                "focused changed infrastructure tests",
                [.. InfrastructureTests, "--filter", infrastructureTestFilter.Filter],
                "Only Infrastructure test files changed; run the touched test classes.",
                RepositoryTestProject.Infrastructure,
                infrastructureTestFilter.TestClasses));
        }
        else if (touchesInfrastructure || touchesNonDashboardApp || touchesScriptsOrConfig)
        {
            checks.Add(new RepositoryTestImpactCheck(
                "infrastructure tests",
                InfrastructureTests,
                infrastructureTestFilter.AbandonReason ??
                (touchesNonDashboardApp && appSubsystems.Length > 1
                    ? "Multiple App subsystems changed; run the full Infrastructure test suite."
                    : touchesNonDashboardApp
                    ? "App behavior lacks a focused test-impact mapping; run the full Infrastructure test suite."
                    : "Infrastructure, script, or configuration behavior changed."),
                RepositoryTestProject.Infrastructure));
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
                    "core tests",
                    CoreTests,
                    summary,
                    RepositoryTestProject.Core),
                new RepositoryTestImpactCheck(
                    "infrastructure tests",
                    InfrastructureTests,
                    summary,
                    RepositoryTestProject.Infrastructure),
                new RepositoryTestImpactCheck(
                    "provider environment tests",
                    ProviderEnvironmentTests,
                    summary,
                    RepositoryTestProject.ProviderEnvironment),
                new RepositoryTestImpactCheck(
                    "cli tests",
                    CliTests,
                    summary,
                    RepositoryTestProject.Cli),
                new RepositoryTestImpactCheck(
                    "full dotnet tests: dashboard",
                    DashboardTests,
                    summary,
                    RepositoryTestProject.Dashboard)
            ]);

    private static bool StartsWith(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static TestClassFilterBuildResult BuildChangedTestClassFilter(
        RepositoryChangeSummary summary,
        string testProjectPrefix,
        ITestClassDeclarationReader declarationReader)
    {
        var changedTestSources = summary.Files
            .Where(file => StartsWith(file.Path, testProjectPrefix))
            .Where(file => Path.GetExtension(file.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (changedTestSources.Length == 0)
        {
            return new TestClassFilterBuildResult(null, null, []);
        }

        var classFilters = new List<string>();
        TestClassDeclarations? projectDeclarations = null;
        foreach (var file in changedTestSources)
        {
            var fileName = Path.GetFileName(file.Path);
            var baseName = Path.GetFileNameWithoutExtension(file.Path);
            var declarations = declarationReader.ReadFile(file.Path);
            if (declarations.Outcome == TestClassDeclarationOutcome.Resolved)
            {
                classFilters.AddRange(declarations.ClassNames.Select(name => $"FullyQualifiedName~{name}"));

                if (!baseName.Contains('.', StringComparison.Ordinal) &&
                    !declarations.ClassNames.Contains(baseName, StringComparer.OrdinalIgnoreCase))
                {
                    projectDeclarations ??= declarationReader.ReadProject(testProjectPrefix);
                    var stemResolves = projectDeclarations.Outcome == TestClassDeclarationOutcome.Resolved
                        ? projectDeclarations.ClassNames.Any(name =>
                            name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                        : declarations.ClassNames.Any(name =>
                            name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
                    if (stemResolves)
                    {
                        classFilters.Add($"FullyQualifiedName~{baseName}");
                    }
                    else if (projectDeclarations.Outcome != TestClassDeclarationOutcome.Resolved)
                    {
                        return new TestClassFilterBuildResult(
                            null,
                            $"Focused test selection was abandoned because the legacy selector for {fileName} could not be resolved against project declarations; run the project unfiltered.",
                            []);
                    }
                }

                continue;
            }

            if (declarations.Outcome == TestClassDeclarationOutcome.NoQualifyingClass)
            {
                return new TestClassFilterBuildResult(
                    null,
                    $"Focused test selection was abandoned because {fileName} declares no qualifying test class; run the project unfiltered.",
                    []);
            }

            if (declarations.Outcome == TestClassDeclarationOutcome.Unreadable)
            {
                return new TestClassFilterBuildResult(
                    null,
                    $"Focused test selection was abandoned because declarations could not be read from {fileName}; run the project unfiltered.",
                    []);
            }

            if (baseName.Contains('.', StringComparison.Ordinal))
            {
                return new TestClassFilterBuildResult(
                    null,
                    $"Focused test selection was abandoned because declarations could not be read from dotted test file {fileName}; run the project unfiltered.",
                    []);
            }

            if (!string.IsNullOrWhiteSpace(baseName))
            {
                // Preserve the legacy behavior when declaration inspection is unavailable.
                classFilters.Add($"FullyQualifiedName~{baseName}");
            }
        }

        var distinctFilters = classFilters
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new TestClassFilterBuildResult(
            distinctFilters.Length == 0 ? null : string.Join("|", distinctFilters),
            null,
            distinctFilters.Select(filter => filter["FullyQualifiedName~".Length..]).ToArray());
    }

    private static string JoinFilters(string left, string? right) =>
        string.IsNullOrWhiteSpace(right) ? left : JoinFilterUnion([left, right]);

    private static string JoinFilterUnion(IEnumerable<string> filters) =>
        string.Join(
            "|",
            filters
                .Where(filter => !string.IsNullOrWhiteSpace(filter))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(ParenthesizeCompositeFilter));

    private static string ParenthesizeCompositeFilter(string filter)
    {
        var trimmed = filter.Trim();
        if (trimmed.StartsWith("(", StringComparison.Ordinal) &&
            trimmed.EndsWith(")", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return trimmed.Contains('|') ||
            trimmed.Contains('&')
            ? $"({trimmed})"
            : trimmed;
    }

    private static (string Name, string Filter, string Reason, IReadOnlyList<string> TestClasses)? TryBuildFocusedInfrastructureFilter(
        RepositoryChangeSummary summary,
        string[] appSubsystems,
        ITestClassDeclarationReader declarationReader)
    {
        if (!CanUseFocusedAppFilters(summary))
            return null;

        var filters = new List<string>();
        var names = new List<string>();
        var testClasses = new List<string>();
        foreach (var subsystem in appSubsystems.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsDashboardSubsystem(subsystem))
                continue;

            if (subsystem.Equals("cli", StringComparison.OrdinalIgnoreCase))
            {
                filters.Add(CliInfrastructureFilter);
                names.Add("CLI");
                testClasses.AddRange(CliInfrastructureClasses);
                continue;
            }

            if (subsystem.Equals("orchestration", StringComparison.OrdinalIgnoreCase))
            {
                var orchestrationBaseNames = summary.Files
                    .Where(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.App/Orchestration/"))
                    .Select(file => Path.GetFileNameWithoutExtension(file.Path))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (orchestrationBaseNames.Length == 0 ||
                    orchestrationBaseNames.Any(name => name.Contains('.', StringComparison.Ordinal)))
                    return null;

                var derivedClassNames = orchestrationBaseNames
                    .Select(name => $"{name}Tests")
                    .ToArray();
                var projectDeclarations = declarationReader.ReadProject(
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/");
                if (projectDeclarations.Outcome is not (
                    TestClassDeclarationOutcome.Resolved or
                    TestClassDeclarationOutcome.Unavailable))
                {
                    return null;
                }

                var resolvedClassNames = projectDeclarations.Outcome == TestClassDeclarationOutcome.Unavailable
                    ? derivedClassNames
                    : derivedClassNames
                        .Where(name => projectDeclarations.ClassNames.Contains(name, StringComparer.Ordinal))
                        .ToArray();
                if (resolvedClassNames.Length != derivedClassNames.Length)
                    return null;

                var orchestrationFilters = resolvedClassNames
                    .Select(name => $"FullyQualifiedName~{name}")
                    .ToArray();

                filters.Add(JoinFilterUnion(orchestrationFilters));
                names.Add("orchestration");
                testClasses.AddRange(resolvedClassNames);
                continue;
            }

            return null;
        }

        if (filters.Count == 0)
            return null;

        return (
            filters.Count == 1
                ? $"focused {names[0]} infrastructure tests"
                : $"focused {string.Join("+", names)} infrastructure tests",
            JoinFilterUnion(filters),
            filters.Count == 1
                ? $"{names[0]}-only App change; run the mapped Infrastructure test partition."
                : $"{string.Join(" and ", names)} App changes; run the union of mapped Infrastructure test partitions.",
            testClasses.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string BuildClassFilter(IEnumerable<string> testClasses) =>
        string.Join("|", testClasses.Select(testClass => $"FullyQualifiedName~{testClass}"));

    private static bool CanUseFocusedAppFilters(RepositoryChangeSummary summary) =>
        !summary.Files.Any(file =>
            StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure/") ||
            StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure.Providers/") ||
            file.Categories.Contains(RepositoryChangeCategory.Script) ||
            file.Categories.Contains(RepositoryChangeCategory.Configuration)) &&
        summary.Files.Count(file => file.Categories.Contains(RepositoryChangeCategory.Source)) <= 5;

    private static bool IsDashboardSubsystem(string subsystem) =>
        subsystem.Equals("dashboard", StringComparison.OrdinalIgnoreCase) ||
        subsystem.Equals("api", StringComparison.OrdinalIgnoreCase);

    private static string? AppSubsystem(string path)
    {
        if (StartsWith(path, "src/Mcg.AgentOrchestrator.App/Cli/"))
            return "cli";

        if (StartsWith(path, "src/Mcg.AgentOrchestrator.App/Dashboard/Api/"))
            return "api";

        if (StartsWith(path, "src/Mcg.AgentOrchestrator.App/Dashboard/"))
            return "dashboard";

        if (StartsWith(path, "src/Mcg.AgentOrchestrator.App/Orchestration/"))
            return "orchestration";

        return null;
    }
}
