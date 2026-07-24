using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public enum RepositoryChangeCategory
{
    Source,
    Test,
    Documentation,
    Configuration,
    BuildSystem,
    GeneratedArtifact,
    Script,
    Unknown
}

public sealed record RepositoryChangedFile(
    string Path,
    IReadOnlyList<RepositoryChangeCategory> Categories,
    bool IsGeneratedArtifact,
    bool IsSecuritySensitive,
    bool RequiresBroadVerification);

public sealed record RepositoryChangeSummary(
    IReadOnlyList<RepositoryChangedFile> Files,
    bool IsDocsOnly,
    bool HasBehaviorChanges,
    bool HasGeneratedArtifacts,
    bool HasBuildSystemChanges,
    bool RequiresConductorRelaunch,
    bool HasSecuritySensitiveChanges,
    bool RequiresBroadVerification,
    string RecommendedVerification);

public sealed record AcceptanceManifestTrustDecision(
    bool RequiresTrustedReview,
    IReadOnlyList<string> SecurityCriticalChanges,
    string Evidence);

public static class RepositoryChangeClassifier
{
    private static readonly string[] GeneratedSegments =
    [
        "bin",
        "obj",
        ".scratch",
        ".orchestrator-prototype",
        ".orchestrator-worktrees",
        "TestResults",
        "playwright-report"
    ];

    private static readonly string[] BuildSystemFileNames =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Build.rsp",
        "Directory.Packages.props",
        "global.json",
        "NuGet.Config"
    ];

    private static readonly string[] SecuritySignals =
    [
        "auth",
        "credential",
        "permission",
        "policy",
        "secret",
        "sandbox",
        "security",
        "token"
    ];

    private static readonly string[] ConductorRelaunchPathPrefixes =
    [
        "src/Mcg.AgentOrchestrator.App/Orchestration/Conductor",
        "src/Mcg.AgentOrchestrator.App/Orchestration/Acceptance",
        "src/Mcg.AgentOrchestrator.App/Orchestration/Dispatch",
        "src/Mcg.AgentOrchestrator.App/Orchestration/GoalRefinementGate.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/LandingExecutor.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/OrchestratorEntityResolver.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/SemanticAcceptance",
        "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Goals",
        "src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner",
        "src/Mcg.AgentOrchestrator.App/Dashboard/Api/GoalManagementCommandService.Dispatches",
        "src/Mcg.AgentOrchestrator.App/Providers/",
        "src/Mcg.AgentOrchestrator.App/SubscriptionPlanning/",
        "src/Mcg.AgentOrchestrator.App/Program.cs",
        "src/Mcg.AgentOrchestrator.Core/Conductor/",
        "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs",
        "src/Mcg.AgentOrchestrator.Core/Application/RepositoryChangeClassifier",
        "src/Mcg.AgentOrchestrator.Core/Application/TaskComplexityEstimator",
        "src/Mcg.AgentOrchestrator.Core/Application/LandingDecision",
        "src/Mcg.AgentOrchestrator.Core/Application/VerificationPolicyCompiler",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workers/",
        "src/Mcg.AgentOrchestrator.Infrastructure/Processes/",
        "src/Mcg.AgentOrchestrator.Infrastructure/Persistence/ModelFunction",
        "src/Mcg.AgentOrchestrator.Infrastructure/Persistence/AgentCatalogStore",
        "config/acceptance-manifest.json",
        "scripts/resolve-run-dir.ps1",
        "scripts/Update-AppDllGitHeadMarker.ps1",
        "mcg-orchestrator.cmd"
    ];

    public static RepositoryChangeSummary Classify(IEnumerable<string> paths)
    {
        var files = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(ClassifyFile)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var isDocsOnly = files.Length > 0 &&
            files.All(file => file.Categories.SequenceEqual([RepositoryChangeCategory.Documentation]));
        var hasGenerated = files.Any(file => file.IsGeneratedArtifact);
        var hasBuild = files.Any(file => file.Categories.Contains(RepositoryChangeCategory.BuildSystem));
        var requiresConductorRelaunch = hasBuild || files.Any(file =>
            ConductorRelaunchPathPrefixes.Any(prefix =>
                file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        var hasSecurity = files.Any(file => file.IsSecuritySensitive);
        var requiresBroad = files.Any(file => file.RequiresBroadVerification);
        var hasBehavior = files.Any(file =>
            file.Categories.Any(category => category is RepositoryChangeCategory.Source or
                RepositoryChangeCategory.Configuration or
                RepositoryChangeCategory.BuildSystem or
                RepositoryChangeCategory.Script));

        return new RepositoryChangeSummary(
            files,
            isDocsOnly,
            hasBehavior,
            hasGenerated,
            hasBuild,
            requiresConductorRelaunch,
            hasSecurity,
            requiresBroad,
            BuildRecommendation(isDocsOnly, hasGenerated, hasBuild, hasSecurity, requiresBroad, hasBehavior));
    }

    public static AcceptanceManifestTrustDecision ClassifyAcceptanceManifestChange(
        string trustedManifestJson,
        string candidateManifestJson)
    {
        using var trusted = JsonDocument.Parse(trustedManifestJson);
        using var candidate = JsonDocument.Parse(candidateManifestJson);
        var changed = new List<string>();
        CompareSecurityCriticalField(
            trusted.RootElement,
            candidate.RootElement,
            "engine.mtpInvocations[].executablePathTemplate",
            invocation => invocation.TryGetProperty("executablePathTemplate", out var value) ? value.GetString() : null,
            changed);
        CompareSecurityCriticalField(
            trusted.RootElement,
            candidate.RootElement,
            "engine.mtpInvocations[].firewallExecutablePathTemplate",
            invocation => invocation.TryGetProperty("firewallExecutablePathTemplate", out var value) ? value.GetString() : null,
            changed);

        return changed.Count == 0
            ? new AcceptanceManifestTrustDecision(
                false,
                [],
                "positive evidence: security-critical MTP executable and firewall path templates are unchanged")
            : new AcceptanceManifestTrustDecision(
                true,
                changed,
                $"trusted review required for changed field(s): {string.Join(", ", changed)}");
    }

    private static RepositoryChangedFile ClassifyFile(string rawPath)
    {
        var path = Normalize(rawPath);
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        var categories = new HashSet<RepositoryChangeCategory>();
        var generated = HasSegment(path, GeneratedSegments);

        if (generated)
        {
            categories.Add(RepositoryChangeCategory.GeneratedArtifact);
        }

        if (IsDocumentation(path, extension))
        {
            categories.Add(RepositoryChangeCategory.Documentation);
        }

        if (IsBuildSystem(path, fileName, extension))
        {
            categories.Add(RepositoryChangeCategory.BuildSystem);
        }

        if (IsTest(path, fileName))
        {
            categories.Add(RepositoryChangeCategory.Test);
        }

        if (IsConfiguration(path, extension))
        {
            categories.Add(RepositoryChangeCategory.Configuration);
        }

        if (IsScript(extension))
        {
            categories.Add(RepositoryChangeCategory.Script);
        }

        if (IsSource(path, extension))
        {
            categories.Add(RepositoryChangeCategory.Source);
        }

        if (categories.Count == 0)
        {
            categories.Add(RepositoryChangeCategory.Unknown);
        }

        var securitySensitive = SecuritySignals.Any(signal => path.Contains(signal, StringComparison.OrdinalIgnoreCase));
        var broad = generated ||
            categories.Contains(RepositoryChangeCategory.BuildSystem) ||
            securitySensitive ||
            path.StartsWith("src/Mcg.AgentOrchestrator.Core/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("src/Mcg.AgentOrchestrator.Infrastructure/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("src/Mcg.AgentOrchestrator.App/Dashboard/Api/", StringComparison.OrdinalIgnoreCase);

        return new RepositoryChangedFile(
            path,
            categories.OrderBy(category => category.ToString(), StringComparer.Ordinal).ToArray(),
            generated,
            securitySensitive,
            broad);
    }

    private static void CompareSecurityCriticalField(
        JsonElement trustedRoot,
        JsonElement candidateRoot,
        string fieldName,
        Func<JsonElement, string?> selector,
        ICollection<string> changed)
    {
        var trustedValues = ReadMtpInvocationValues(trustedRoot, selector);
        var candidateValues = ReadMtpInvocationValues(candidateRoot, selector);
        if (!trustedValues.SequenceEqual(candidateValues, StringComparer.Ordinal))
        {
            changed.Add(fieldName);
        }
    }

    private static string[] ReadMtpInvocationValues(
        JsonElement root,
        Func<JsonElement, string?> selector)
    {
        if (!root.TryGetProperty("engine", out var engine) ||
            !engine.TryGetProperty("mtpInvocations", out var invocations) ||
            invocations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return invocations.EnumerateArray()
            .Select(invocation =>
            {
                var project = invocation.TryGetProperty("project", out var projectValue)
                    ? projectValue.GetString()
                    : null;
                return $"{project}\u001f{selector(invocation)}";
            })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildRecommendation(
        bool isDocsOnly,
        bool hasGenerated,
        bool hasBuild,
        bool hasSecurity,
        bool requiresBroad,
        bool hasBehavior)
    {
        if (hasGenerated)
        {
            return "Remove generated artifacts before acceptance, then rerun focused verification.";
        }

        if (hasBuild)
        {
            return "Run the build/test broker or full relevant dotnet test suite because build configuration changed.";
        }

        if (hasSecurity)
        {
            return "Run focused tests plus a security/sandbox review for changed policy-sensitive paths.";
        }

        if (requiresBroad)
        {
            return "Run focused tests and broaden to the owning project because shared infrastructure changed.";
        }

        if (isDocsOnly)
        {
            return "No build required unless documentation tooling changed.";
        }

        return hasBehavior
            ? "Run focused tests covering the changed source and adjacent behavior."
            : "Inspect changed files and run focused verification if behavior is affected.";
    }

    private static bool IsDocumentation(string path, string extension) =>
        path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildSystem(string path, string fileName, string extension) =>
        extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
        BuildSystemFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase) ||
        path.Equals("config/acceptance-manifest.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsTest(string path, string fileName) =>
        path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsConfiguration(string path, string extension) =>
        path.StartsWith("config/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("appsettings", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase);

    private static bool IsScript(string extension) =>
        extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".sh", StringComparison.OrdinalIgnoreCase);

    private static bool IsSource(string path, string extension) =>
        path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) &&
        extension is ".cs" or ".fs" or ".vb";

    private static bool HasSegment(string path, IReadOnlyList<string> segments)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => segments.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('/');
}
