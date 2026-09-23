using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptancePolicyShardPlanner
{
    internal const string CoreProject = "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj";
    internal const string ProvidersProject = "src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj";
    internal const string OperatorCommsProject = "src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/Mcg.AgentOrchestrator.Infrastructure.OperatorComms.csproj";
    internal const string InfrastructureProject = "src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj";
    internal const string AppProject = "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj";
    internal const string CoreTestsProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
    internal const string InfrastructureTestsProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    internal const string AcceptanceTestsProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Acceptance/Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.csproj";
    internal const string DashboardTestsProject = "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj";
    internal const string TestSupportProject = "tests/Mcg.AgentOrchestrator.TestSupport/Mcg.AgentOrchestrator.TestSupport.csproj";
    internal const string ProviderEnvironmentTestsProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj";
    internal const string CliTestsProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";

    private static readonly Dictionary<string, string[]> ReferencingProjectsByProject = new(StringComparer.OrdinalIgnoreCase)
    {
        [CoreProject] = [ProvidersProject, OperatorCommsProject, InfrastructureProject, AppProject, CoreTestsProject, InfrastructureTestsProject, AcceptanceTestsProject, DashboardTestsProject, TestSupportProject, ProviderEnvironmentTestsProject, CliTestsProject],
        [ProvidersProject] = [InfrastructureProject, AppProject, InfrastructureTestsProject, ProviderEnvironmentTestsProject],
        [OperatorCommsProject] = [AppProject, InfrastructureTestsProject, DashboardTestsProject, TestSupportProject, ProviderEnvironmentTestsProject, CliTestsProject],
        [InfrastructureProject] = [AppProject, InfrastructureTestsProject, AcceptanceTestsProject, DashboardTestsProject, TestSupportProject, ProviderEnvironmentTestsProject, CliTestsProject],
        [AppProject] = [InfrastructureTestsProject, DashboardTestsProject, TestSupportProject, ProviderEnvironmentTestsProject, CliTestsProject],
        [CoreTestsProject] = [],
        [InfrastructureTestsProject] = [],
        [AcceptanceTestsProject] = [],
        [DashboardTestsProject] = [],
        [TestSupportProject] = [InfrastructureTestsProject, DashboardTestsProject],
        [ProviderEnvironmentTestsProject] = [],
        [CliTestsProject] = []
    };

    internal static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/').Trim();

    internal static bool ChangeScopedAcceptanceEnabled()
    {
        var value = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        return string.IsNullOrWhiteSpace(value) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool StructuralCoverageApplies(
        AcceptanceGateEngineSettings engineSettings,
        IReadOnlyList<string>? changedFiles) =>
        engineSettings.EnforceStructuralCoverage &&
        ClassifyDotnetShardDisposition(changedFiles) == DotnetShardDisposition.RunDotnetShards &&
        (changedFiles is null ||
            changedFiles.Count == 0 ||
            !RepositoryChangeClassifier.Classify(changedFiles).IsDocsOnly);

    internal static DotnetShardDisposition ClassifyDotnetShardDisposition(
        IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null)
            return DotnetShardDisposition.RunDotnetShards;

        if (changedFiles.Count == 0)
            return DotnetShardDisposition.KnownEmptyCandidate;

        return changedFiles.All(IsStrictDocsTreePath)
            ? DotnetShardDisposition.DocsTreeOnlyCandidate
            : DotnetShardDisposition.RunDotnetShards;
    }

    private static bool IsStrictDocsTreePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.Equals(path.Trim(), StringComparison.Ordinal) ||
            Path.IsPathRooted(path) ||
            path.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        return segments.Length > 1 &&
            segments[0].Equals("docs", StringComparison.OrdinalIgnoreCase) &&
            segments.All(segment =>
                !string.IsNullOrWhiteSpace(segment) &&
                !segment.Equals(".", StringComparison.Ordinal) &&
                !segment.Equals("..", StringComparison.Ordinal));
    }

    internal static string DotnetShardDispositionReason(DotnetShardDisposition disposition) =>
        disposition switch
        {
            DotnetShardDisposition.KnownEmptyCandidate => "known-empty-candidate",
            DotnetShardDisposition.DocsTreeOnlyCandidate => "docs-tree-only-candidate",
            _ => "run-dotnet-shards"
        };

    internal static IReadOnlyList<AcceptanceManifestCheck> ApplyDotnetShardDisposition(
        IReadOnlyList<AcceptanceManifestCheck> checks,
        DotnetShardDisposition disposition)
    {
        if (disposition == DotnetShardDisposition.RunDotnetShards)
            return checks;

        var reason = DotnetShardDispositionReason(disposition);
        return
        [
            .. checks.Where(check =>
                !check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                !check.Name.Equals(reason, StringComparison.Ordinal)),
            new AcceptanceManifestCheck
            {
                Name = reason,
                Type = "no-op",
                Arguments = ["dotnet-shards=omitted"]
            }
        ];
    }

    internal static IReadOnlyList<AcceptanceTestLane> SelectInfrastructureTestLanes(
        IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<string>? changedFiles,
        PolicyShardPlan policyShardPlan)
    {
        if (!policyShardPlan.Applies || policyShardPlan.ForceFull || changedFiles is null)
        {
            return lanes;
        }

        var normalizedFiles = changedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedFiles.Length == 0)
        {
            return lanes;
        }

        var summary = RepositoryChangeClassifier.Classify(normalizedFiles);
        if (summary.HasBuildSystemChanges || summary.HasSecuritySensitiveChanges)
        {
            return lanes;
        }

        return lanes.Where(lane => !lane.RequiresBuildSystemChange).ToArray();
    }

    internal static PolicyShardPlan BuildPolicyShardPlan(IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return PolicyShardPlan.NotApplicable("no changed files");

        var normalizedFiles = changedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedFiles.Length == 0)
            return PolicyShardPlan.NotApplicable("no changed files");

        var summary = RepositoryChangeClassifier.Classify(normalizedFiles);
        var changedProjects = normalizedFiles
            .Select(TryMapPathToProject)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var closure = BuildProjectDependencyClosure(changedProjects);
        var evidence = BuildPolicyShardEvidence(changedProjects, closure);
        var fullShardReason = FullShardReason(normalizedFiles, summary, changedProjects);
        return fullShardReason is not null
            ? PolicyShardPlan.Full($"{fullShardReason}; {evidence}", closure)
            : PolicyShardPlan.Scoped(evidence, closure);
    }

    private static string? FullShardReason(
        IReadOnlyList<string> changedFiles,
        RepositoryChangeSummary summary,
        IReadOnlyList<string> changedProjects)
    {
        if (FullShardOverrideEnabled())
            return "MCG_ACCEPTANCE_FULL_SHARDS=1";

        if (!ChangeScopedAcceptanceEnabled())
            return "MCG_ACCEPTANCE_CHANGE_SCOPED disabled";

        foreach (var path in changedFiles)
        {
            var fileName = Path.GetFileName(path);
            var extension = Path.GetExtension(path);
            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            {
                return $"build-system file changed: {path}";
            }

            if (path.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
                return $"script changed: {path}";

        }

        if (!summary.IsDocsOnly && summary.HasBehaviorChanges && changedProjects.Count == 0)
            return "changed files did not map to a known project";

        return null;
    }

    private static bool FullShardOverrideEnabled()
    {
        var value = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS");
        return value is not null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> BuildProjectDependencyClosure(IReadOnlyList<string> changedProjects)
    {
        var closure = new HashSet<string>(changedProjects, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(changedProjects);
        while (pending.Count > 0)
        {
            var project = pending.Dequeue();
            if (!ReferencingProjectsByProject.TryGetValue(project, out var referencingProjects))
                continue;

            foreach (var referencingProject in referencingProjects)
            {
                if (closure.Add(referencingProject))
                    pending.Enqueue(referencingProject);
            }
        }

        return closure;
    }

    internal static string BuildPolicyShardEvidence(
        IReadOnlyList<string> changedProjects,
        IReadOnlySet<string> closure)
    {
        var changed = changedProjects.Count == 0
            ? "(none)"
            : string.Join(", ", changedProjects.Select(ProjectLabel));
        var affected = closure.Count == 0
            ? "(none)"
            : string.Join(", ", closure.OrderBy(ProjectLabel, StringComparer.OrdinalIgnoreCase).Select(ProjectLabel));
        return $"changed projects: {changed}; dependency closure: {affected}";
    }

    private static string? TryMapPathToProject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = NormalizePath(path)!;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.Core/", StringComparison.OrdinalIgnoreCase))
            return CoreProject;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.Infrastructure.Providers/", StringComparison.OrdinalIgnoreCase))
            return ProvidersProject;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/", StringComparison.OrdinalIgnoreCase))
            return OperatorCommsProject;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.Infrastructure/", StringComparison.OrdinalIgnoreCase))
            return InfrastructureProject;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.App/", StringComparison.OrdinalIgnoreCase))
            return AppProject;
        if (normalized.StartsWith("tests/Mcg.AgentOrchestrator.Core.Tests/", StringComparison.OrdinalIgnoreCase))
            return CoreTestsProject;
        if (normalized.StartsWith("tests/Mcg.AgentOrchestrator.Dashboard.Tests/", StringComparison.OrdinalIgnoreCase))
            return DashboardTestsProject;
        if (normalized.StartsWith("tests/Mcg.AgentOrchestrator.TestSupport/", StringComparison.OrdinalIgnoreCase))
            return TestSupportProject;
        if (normalized.StartsWith("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/", StringComparison.OrdinalIgnoreCase))
            return ProviderEnvironmentTestsProject;
        if (normalized.StartsWith("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/", StringComparison.OrdinalIgnoreCase))
            return CliTestsProject;
        if (normalized.StartsWith("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/", StringComparison.OrdinalIgnoreCase))
            return InfrastructureTestsProject;
        return null;
    }

    internal static string ProjectLabel(string project) =>
        project.Equals(CoreProject, StringComparison.OrdinalIgnoreCase) ? "Core" :
        project.Equals(ProvidersProject, StringComparison.OrdinalIgnoreCase) ? "Infrastructure.Providers" :
        project.Equals(OperatorCommsProject, StringComparison.OrdinalIgnoreCase) ? "Infrastructure.OperatorComms" :
        project.Equals(InfrastructureProject, StringComparison.OrdinalIgnoreCase) ? "Infrastructure" :
        project.Equals(AppProject, StringComparison.OrdinalIgnoreCase) ? "App" :
        project.Equals(CoreTestsProject, StringComparison.OrdinalIgnoreCase) ? "Core.Tests" :
        project.Equals(InfrastructureTestsProject, StringComparison.OrdinalIgnoreCase) ? "Infrastructure.Tests" :
        project.Equals(DashboardTestsProject, StringComparison.OrdinalIgnoreCase) ? "Dashboard.Tests" :
        project.Equals(TestSupportProject, StringComparison.OrdinalIgnoreCase) ? "TestSupport" :
        IsExtractedInfrastructureProject(project) ? ExtractedInfrastructureProjectLabel(project) :
        project;

    private static string ExtractedInfrastructureProjectLabel(string project)
    {
        var label = Path.GetFileNameWithoutExtension(NormalizePath(project));
        const string prefix = "Mcg.AgentOrchestrator.";
        return label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? label[prefix.Length..]
            : label;
    }

    internal static bool IsBroadInfrastructureTestCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        ProjectMatches(check.Project, InfrastructureTestsProject) &&
        !check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    internal static bool IsFocusedProjectDotnetCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        check.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    internal static bool IsFullPolicyShardCheck(AcceptanceManifestCheck check) =>
        IsPolicyShardProjectCheck(check) &&
        !check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    internal static bool IsPolicyShardProjectCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        (ProjectMatches(check.Project, CoreTestsProject) ||
         ProjectMatches(check.Project, InfrastructureTestsProject) ||
         IsDashboardTestProject(check.Project) ||
         IsExtractedInfrastructureProject(check.Project));

    internal static bool IsSkippedPolicyShardCheck(AcceptanceManifestCheck check, PolicyShardPlan policyShardPlan) =>
        policyShardPlan.Applies &&
        !policyShardPlan.ForceFull &&
        IsFullPolicyShardCheck(check) &&
        !policyShardPlan.IncludesProject(check.Project);

    internal static bool IsRunnablePolicyShardCheck(AcceptanceManifestCheck check, PolicyShardPlan policyShardPlan) =>
        policyShardPlan.Applies &&
        !policyShardPlan.ForceFull &&
        IsPolicyShardProjectCheck(check) &&
        policyShardPlan.IncludesProject(check.Project);

    internal static bool IsExtractedInfrastructureProject(string project)
    {
        var normalized = NormalizePath(project);
        const string infrastructureTestsRoot = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.IndexOf(infrastructureTestsRoot, StringComparison.OrdinalIgnoreCase) < 0 ||
            normalized.EndsWith(InfrastructureTestsProject, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalized);
        return fileName.StartsWith("Mcg.AgentOrchestrator.Infrastructure.", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ProjectMatches(string project, string expectedProject) =>
        NormalizePath(project)?.EndsWith(
            NormalizePath(expectedProject),
            StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsDashboardTestProject(string project) =>
        project.EndsWith(
            DashboardTestsProject,
            StringComparison.OrdinalIgnoreCase) ||
        project.EndsWith(
            "tests\\Mcg.AgentOrchestrator.Dashboard.Tests\\Mcg.AgentOrchestrator.Dashboard.Tests.csproj",
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsReplacedByFocusedProjectCheck(
        AcceptanceManifestCheck manifestCheck,
        IReadOnlyList<AcceptanceManifestCheck> focusedProjectChecks) =>
        manifestCheck.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(manifestCheck.Project) &&
        manifestCheck.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        !manifestCheck.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase)) &&
        focusedProjectChecks.Any(focused =>
            string.Equals(NormalizePath(focused.Project), NormalizePath(manifestCheck.Project), StringComparison.OrdinalIgnoreCase));

    internal static void AddPolicyShardReceiptResults(
        List<AcceptanceCheckResult> checks,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        PolicyShardPlan policyShardPlan)
    {
        if (!policyShardPlan.Applies || policyShardPlan.ForceFull)
            return;

        foreach (var shard in manifestChecks.Where(IsFullPolicyShardCheck))
        {
            var existingIndex = checks.FindIndex(result =>
                result.Name.Equals(shard.Name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                var existing = checks[existingIndex];
                var executionEvidence = $"changed file in dependency closure; {policyShardPlan.Evidence}";
                checks[existingIndex] = existing with
                {
                    ResultSummary = string.IsNullOrWhiteSpace(existing.ResultSummary)
                        ? executionEvidence
                        : $"{existing.ResultSummary}; {executionEvidence}"
                };
                continue;
            }

            if (!policyShardPlan.IncludesProject(shard.Project))
            {
                checks.Add(new AcceptanceCheckResult(
                    shard.Name,
                    true,
                    null,
                    null,
                    ResultSummary:
                    $"skipped: no changed file in dependency closure; shard project: {ProjectLabel(NormalizePath(shard.Project)!)}; {policyShardPlan.Evidence}"));
                continue;
            }

            var coveringCheck = effectiveChecks.FirstOrDefault(check =>
                IsPolicyShardProjectCheck(check) &&
                string.Equals(NormalizePath(check.Project), NormalizePath(shard.Project), StringComparison.OrdinalIgnoreCase));
            if (coveringCheck is null)
                continue;

            var coveringResult = checks.FirstOrDefault(result =>
                result.Name.Equals(coveringCheck.Name, StringComparison.OrdinalIgnoreCase));
            if (coveringResult is null)
                continue;

            checks.Add(new AcceptanceCheckResult(
                shard.Name,
                coveringResult.Passed,
                coveringResult.ExitCode,
                coveringResult.Passed ? null : coveringResult.OutputTail,
                coveringResult.ArtifactsPath,
                coveringResult.BrokerName,
                coveringResult.LeaseId,
                coveringResult.DurationMilliseconds,
                coveringResult.LockRemediationApplied,
                $"covered by: {coveringCheck.Name}; changed file in dependency closure; {policyShardPlan.Evidence}",
                CoveredBy: [coveringCheck.Name]));
        }
    }
}

internal sealed record PolicyShardPlan(
    bool Applies,
    bool ForceFull,
    string Evidence,
    IReadOnlySet<string> DependencyClosure)
{
    public static PolicyShardPlan NotApplicable(string evidence) =>
        new(false, false, evidence, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public static PolicyShardPlan Full(string evidence, IReadOnlySet<string> dependencyClosure) =>
        new(true, true, evidence, dependencyClosure);

    public static PolicyShardPlan Scoped(string evidence, IReadOnlySet<string> dependencyClosure) =>
        new(true, false, evidence, dependencyClosure);

    public bool IncludesProject(string? project) =>
        ForceFull ||
        !Applies ||
        (!string.IsNullOrWhiteSpace(project) &&
            DependencyClosure.Contains(AcceptancePolicyShardPlanner.NormalizePath(project)!));
}
