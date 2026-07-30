using System.Text.RegularExpressions;

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
        "FullyQualifiedName~CliCommandTests|FullyQualifiedName~CliHelpTests";

    private const string DashboardInfrastructureFilter =
        "FullyQualifiedName~DashboardRenderingTests|FullyQualifiedName~DashboardHostTests&Category!=HostIntegration|FullyQualifiedName~DashboardValidationHarnessTests";

    private static readonly FocusedInfrastructureCheck[] ConductorInfrastructureChecks =
    [
        new(
            "infrastructure tests: Conductor control",
            "FullyQualifiedName~ConductorDriverTests|FullyQualifiedName~AdvanceLoopTests|FullyQualifiedName~ConductWatchSweepScopingTests|FullyQualifiedName~ConductorLoopHandoffTests|FullyQualifiedName~ConductorWakeSignalTests|FullyQualifiedName~ConductorDriverTestsOperatorDispositionSnapshots",
            "Conductor control-flow change; run the mapped driver, advance, watch, and handoff contracts."),
        new(
            "infrastructure tests: Conductor acceptance",
            "FullyQualifiedName~ConductorBatchLoopTests|FullyQualifiedName~ConductorBatchLoopVerificationReconcileTests|FullyQualifiedName~LandingExecutorTests",
            "Conductor landing change; run the mapped batch-loop and landing contracts."),
        new(
            "infrastructure tests: Conductor process",
            "FullyQualifiedName~ConductorSelfRelaunchTests|FullyQualifiedName~ConductorSuccessorSelfCheckTests",
            "Conductor process-lifecycle change; run the real relaunch and successor self-check contracts.")
    ];

    private static readonly FocusedInfrastructureCheck[] WorkerInfrastructureChecks =
    [
        new(
            "infrastructure tests: Worker dispatch results",
            "FullyQualifiedName~WorkerDispatchTestsWorkerResultClassification|FullyQualifiedName~WorkerDispatchSpecClarificationTests|FullyQualifiedName~WorkerDispatchAcceptanceAdmissionTests|FullyQualifiedName~WorkerDispatchJobAccountingTests|FullyQualifiedName~WorkerResultParserEvidenceTests|FullyQualifiedName~InquiryDispatcherTests|FullyQualifiedName~ChaosGateWorkerResult|FullyQualifiedName~ChaosGateNoWorkerResultTests|FullyQualifiedName~ChaosGateCommittedWorkerResultFileTests|FullyQualifiedName~ChaosGateUntrackedWorkerResultFileTests",
            "Worker result handling changed; run classification and parser evidence contracts."),
        new(
            "infrastructure tests: Worker dispatch model and sandbox",
            "FullyQualifiedName~WorkerDispatchTestsModelSelection|FullyQualifiedName~WorkerDispatchTestsSandboxLowIntegrity|FullyQualifiedName~WorkerSandboxCapabilityPlannerTests",
            "Worker dispatch changed; run model-selection and sandbox contracts."),
        new(
            "infrastructure tests: Worker dispatch preparation",
            "FullyQualifiedName~WorkerDispatchTestsDispatchPreparation|FullyQualifiedName~WorkerDispatchTestsSubscriptionPreflight|FullyQualifiedName~WorkerProfileTests|FullyQualifiedName~WorkerContextArtifactsCharacterizationTests|FullyQualifiedName~WorkerContextArtifactsVerificationTests|FullyQualifiedName~RealWorkerProcessGuardTests|FullyQualifiedName~WorkerProcessJobsTests|FullyQualifiedName~WorkerShellTests",
            "Worker dispatch changed; run preparation, preflight, profile, process-guard, and job contracts."),
        new(
            "infrastructure tests: Worker dispatch orchestration",
            "FullyQualifiedName~AdvanceLoopTests|FullyQualifiedName~ConductLoopLockTests|FullyQualifiedName~ConductorLoopHandoffTests|FullyQualifiedName~GoalLifecycleEventWriterTests",
            "Worker dispatch changed; run the mapped orchestration and lifecycle contracts.")
    ];

    // The full suite is expressed as the two per-project runs rather than one solution-level
    // "dotnet test": both test projects are Microsoft.Testing.Platform, and a project-less
    // dotnet-test check cannot be routed to the MTP runner, so it always dies on .NET 10 with
    // "Testing with VSTest target is no longer supported".

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
        var touchesCoreSource = summary.Files.Any(file =>
            StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Core/"));
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
        var focusedInfrastructureChecks = TryBuildFocusedInfrastructureChecks(summary, appSubsystems);

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

        if (focusedInfrastructureChecks is not null)
        {
            foreach (var focusedCheck in focusedInfrastructureChecks)
            {
                checks.Add(new RepositoryTestImpactCheck(
                    focusedCheck.Name,
                    [.. InfrastructureTests, "--filter", focusedCheck.Filter],
                    focusedCheck.Reason));
            }

            if (infrastructureTestFilter is not null &&
                !ChangedInfrastructureTestsAreCovered(summary, focusedInfrastructureChecks))
            {
                checks.Add(new RepositoryTestImpactCheck(
                    "focused changed infrastructure tests",
                    [.. InfrastructureTests, "--filter", infrastructureTestFilter],
                    "Changed Infrastructure test classes are outside the mapped source partitions; run them explicitly."));
            }
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
        else if (touchesCoreSource || touchesInfrastructure || touchesApp || touchesScriptsOrConfig)
        {
            checks.Add(new RepositoryTestImpactCheck(
                "infrastructure tests",
                InfrastructureTests,
                touchesApp && appSubsystems.Length > 1
                    ? "Multiple App subsystems changed; run the full Infrastructure test suite."
                    : touchesApp
                    ? "App behavior lacks a focused test-impact mapping; run the full Infrastructure test suite."
                    : touchesCoreSource
                    ? "Core behavior lacks a focused reverse-dependency mapping; retain the full Infrastructure integration suite."
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
                    "full dotnet tests: core",
                    CoreTests,
                    summary),
                new RepositoryTestImpactCheck(
                    "full dotnet tests: infrastructure",
                    InfrastructureTests,
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

    private static IReadOnlyList<FocusedInfrastructureCheck>? TryBuildFocusedInfrastructureChecks(
        RepositoryChangeSummary summary,
        string[] appSubsystems)
    {
        if (summary.Files.Any(file =>
            file.Categories.Contains(RepositoryChangeCategory.Script) ||
            file.Categories.Contains(RepositoryChangeCategory.Configuration)))
            return null;

        var infrastructureSourceFiles = summary.Files
            .Where(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure/"))
            .ToArray();
        if (infrastructureSourceFiles.Any(file =>
            !StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Infrastructure/Workers/")))
            return null;

        var appSourceFiles = summary.Files
            .Where(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.App/"))
            .ToArray();
        if (appSourceFiles.Any(file => AppSubsystem(file.Path) is null))
            return null;

        var checks = new List<FocusedInfrastructureCheck>();
        var coreSourceFiles = summary.Files
            .Where(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.Core/"))
            .ToArray();
        if (coreSourceFiles.Length > 0)
        {
            var coreChecks = SelectCoreInfrastructureChecks(coreSourceFiles);
            if (coreChecks is null)
                return null;

            checks.AddRange(coreChecks);
        }

        var appFilters = new List<string>();
        var names = new List<string>();
        foreach (var subsystem in appSubsystems.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (subsystem.Equals("cli", StringComparison.OrdinalIgnoreCase))
            {
                appFilters.Add(CliInfrastructureFilter);
                names.Add("CLI");
                continue;
            }

            if (subsystem.Equals("dashboard", StringComparison.OrdinalIgnoreCase) ||
                subsystem.Equals("api", StringComparison.OrdinalIgnoreCase))
            {
                appFilters.Add(DashboardInfrastructureFilter);
                if (!names.Contains("dashboard", StringComparer.OrdinalIgnoreCase))
                    names.Add("dashboard");
                continue;
            }

            if (subsystem.Equals("orchestration", StringComparison.OrdinalIgnoreCase))
            {
                var orchestrationFilters = summary.Files
                    .Where(file => StartsWith(file.Path, "src/Mcg.AgentOrchestrator.App/Orchestration/"))
                    .Select(file => Path.GetFileNameWithoutExtension(file.Path))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => $"FullyQualifiedName~{name}Tests")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (orchestrationFilters.Length == 0)
                    return null;

                appFilters.Add(JoinFilterUnion(orchestrationFilters));
                names.Add("orchestration");
                continue;
            }

            return null;
        }

        if (appFilters.Count > 0)
        {
            checks.Add(new FocusedInfrastructureCheck(
                appFilters.Count == 1
                    ? $"focused {names[0]} infrastructure tests"
                    : $"focused {string.Join("+", names)} infrastructure tests",
                JoinFilterUnion(appFilters),
                appFilters.Count == 1
                    ? $"{names[0]}-only App change; run the mapped Infrastructure test partition."
                    : $"{string.Join(" and ", names)} App changes; run the union of mapped Infrastructure test partitions."));
        }

        if (infrastructureSourceFiles.Length > 0)
            checks.AddRange(SelectWorkerInfrastructureChecks(infrastructureSourceFiles));

        return checks.Count == 0
            ? null
            : checks
                .GroupBy(check => check.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
    }

    private static IEnumerable<FocusedInfrastructureCheck> SelectConductorInfrastructureChecks(
        IReadOnlyList<RepositoryChangedFile> appSourceFiles)
    {
        var selected = new HashSet<int>();
        foreach (var file in appSourceFiles.Where(file =>
            AppSubsystem(file.Path)?.Equals("conductor", StringComparison.OrdinalIgnoreCase) == true))
        {
            var fileName = Path.GetFileNameWithoutExtension(file.Path);
            if (fileName.Equals("ConductorDriver", StringComparison.OrdinalIgnoreCase))
            {
                selected.Add(0);
            }
            else if (fileName.StartsWith("ConductorBatchLoop", StringComparison.OrdinalIgnoreCase))
            {
                selected.Add(1);
            }
            else if (fileName.StartsWith("ConductorSelfRelaunch", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("ConductorSuccessorSelfCheck", StringComparison.OrdinalIgnoreCase))
            {
                selected.Add(2);
            }
            else
            {
                selected.UnionWith(Enumerable.Range(0, ConductorInfrastructureChecks.Length));
            }
        }

        return selected.Order().Select(index => ConductorInfrastructureChecks[index]);
    }

    private static IEnumerable<FocusedInfrastructureCheck> SelectWorkerInfrastructureChecks(
        IReadOnlyList<RepositoryChangedFile> infrastructureSourceFiles)
    {
        var selected = new HashSet<int>();
        foreach (var file in infrastructureSourceFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file.Path);
            if (fileName.StartsWith("WorkerResultParser", StringComparison.OrdinalIgnoreCase))
            {
                selected.Add(0);
            }
            else if (fileName.StartsWith("WorkerProfile", StringComparison.OrdinalIgnoreCase))
            {
                selected.UnionWith(Enumerable.Range(0, WorkerInfrastructureChecks.Length));
            }
            else
            {
                selected.UnionWith(Enumerable.Range(0, WorkerInfrastructureChecks.Length));
            }
        }

        return selected.Order().Select(index => WorkerInfrastructureChecks[index]);
    }

    private static IReadOnlyList<FocusedInfrastructureCheck>? SelectCoreInfrastructureChecks(
        IReadOnlyList<RepositoryChangedFile> coreSourceFiles)
    {
        var checks = new List<FocusedInfrastructureCheck>();
        foreach (var file in coreSourceFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file.Path);
            if (fileName.StartsWith("ReviewerWorkerResult", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("WorkerResult", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(WorkerInfrastructureChecks[0]);
                continue;
            }

            // Core is referenced by both App and Infrastructure. Unknown Core surfaces therefore
            // retain the broad Infrastructure integration suite instead of silently dropping the
            // reverse dependency from the acceptance plan.
            return null;
        }

        return checks
            .GroupBy(check => check.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool ChangedInfrastructureTestsAreCovered(
        RepositoryChangeSummary summary,
        IReadOnlyList<FocusedInfrastructureCheck> checks)
    {
        var changedClassNames = summary.Files
            .Where(file => StartsWith(file.Path, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/"))
            .Select(file => Path.GetFileNameWithoutExtension(file.Path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return changedClassNames.Length > 0 &&
            changedClassNames.All(className =>
                checks.Any(check =>
                    FilterContainsExactClassOperand(check.Filter, className)));
    }

    private static bool FilterContainsExactClassOperand(string filter, string className) =>
        Regex.Matches(
                filter,
                @"FullyQualifiedName~(?<class>[A-Za-z_][A-Za-z0-9_]*)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups["class"].Value)
            .Any(candidate => candidate.Equals(className, StringComparison.OrdinalIgnoreCase));

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

    private sealed record FocusedInfrastructureCheck(string Name, string Filter, string Reason);
}
