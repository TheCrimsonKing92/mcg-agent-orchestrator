namespace Mcg.AgentOrchestrator.Core;

public enum RepositoryOwnershipArea
{
    Source,
    Test,
    Documentation,
    Configuration,
    BuildSystem,
    GeneratedOrNoisy,
    SharedInfrastructure,
    DashboardApi,
    DashboardUi,
    Script,
    Skill,
    Unknown
}

public sealed record RepositoryOwnedPath(
    string Path,
    RepositoryOwnershipArea Area,
    string ReservationKey,
    bool IsGeneratedOrNoisy,
    bool IsHighRisk,
    bool RequiresSerialization);

public sealed record RepositoryWriteSetGuardReport(
    IReadOnlyList<RepositoryOwnedPath> Paths,
    IReadOnlyList<string> RequiredResources,
    bool RequiresOperatorApproval,
    IReadOnlyList<string> Reasons);

public static class RepositoryOwnershipMap
{
    internal const string TestProjectReservationKeyPrefix = "test-project:tests/";
    internal const string SharedInfrastructureReservationKey = "shared-infrastructure";

    private static readonly Dictionary<string, (string Token, string[] Subsystems)> SharedInfrastructureProjects =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mcg.AgentOrchestrator.Core"] = (
                "core",
                ["Application", "Collaboration", "Conductor", "Domain", "Models", "Persistence", "Reports"]),
            ["Mcg.AgentOrchestrator.Infrastructure"] = (
                "infrastructure",
                ["Diagnostics", "OperatorComms", "Persistence", "Processes", "Verification", "Workers", "Workspaces"]),
            ["Mcg.AgentOrchestrator.Infrastructure.Providers"] = ("infrastructure.providers", [])
        };

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
        "Directory.Packages.props",
        "global.json",
        "NuGet.Config"
    ];

    public static RepositoryOwnedPath Classify(string rawPath)
    {
        var path = Normalize(rawPath);
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (HasSegment(path, GeneratedSegments))
        {
            return Build(path, RepositoryOwnershipArea.GeneratedOrNoisy, "generated-or-noisy", highRisk: true);
        }

        if (path.StartsWith("src/Mcg.AgentOrchestrator.Core/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("src/Mcg.AgentOrchestrator.Infrastructure/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("src/Mcg.AgentOrchestrator.Infrastructure.Providers/", StringComparison.OrdinalIgnoreCase))
        {
            return Build(
                path,
                RepositoryOwnershipArea.SharedInfrastructure,
                SharedInfrastructureReservationKeyFor(path),
                highRisk: true);
        }

        if (path.StartsWith("src/Mcg.AgentOrchestrator.App/Dashboard/Api/", StringComparison.OrdinalIgnoreCase))
        {
            return Build(path, RepositoryOwnershipArea.DashboardApi, "dashboard-api", highRisk: true);
        }

        if (path.StartsWith("src/Mcg.AgentOrchestrator.App/Dashboard/", StringComparison.OrdinalIgnoreCase))
        {
            return Build(path, RepositoryOwnershipArea.DashboardUi, "dashboard-ui", highRisk: false);
        }

        if (IsBuildSystem(path, fileName, extension))
        {
            return Build(path, RepositoryOwnershipArea.BuildSystem, "build-system", highRisk: true);
        }

        if (path.StartsWith(".agents/skills/", StringComparison.OrdinalIgnoreCase))
        {
            return Build(path, RepositoryOwnershipArea.Skill, "worker-skills", highRisk: true);
        }

        if (IsTest(path, fileName))
        {
            var reservationKey = TestProjectReservationKey(path);
            return reservationKey is null
                ? Build(path, RepositoryOwnershipArea.Unknown, "unknown-acceptance-scope", highRisk: false)
                : Build(path, RepositoryOwnershipArea.Test, reservationKey, highRisk: false);
        }

        if (IsDocumentation(path, extension))
        {
            return Build(path, RepositoryOwnershipArea.Documentation, "docs", highRisk: false);
        }

        if (IsScript(extension))
        {
            return Build(path, RepositoryOwnershipArea.Script, "scripts", highRisk: true);
        }

        if (IsConfiguration(path, extension))
        {
            return Build(path, RepositoryOwnershipArea.Configuration, "configuration", highRisk: true);
        }

        if (path.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
        {
            return Build(path, RepositoryOwnershipArea.Source, OwnerKeyForSource(path), highRisk: false, serialize: false);
        }

        return Build(path, RepositoryOwnershipArea.Unknown, OwnerKeyForUnknown(path), highRisk: false, serialize: false);
    }

    public static RepositoryWriteSetGuardReport GuardWriteSet(IEnumerable<string> rawPaths)
    {
        var paths = rawPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Classify)
            .OrderBy(path => path.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resources = paths
            .Where(path => path.RequiresSerialization)
            .Select(path => $"ownership:{path.ReservationKey}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reasons = new List<string>();
        foreach (var path in paths.Where(path => path.IsGeneratedOrNoisy))
        {
            reasons.Add($"generated/noisy path requires operator cleanup before worker start: {path.Path}");
        }

        foreach (var path in paths.Where(path => path.IsHighRisk && !path.IsGeneratedOrNoisy))
        {
            reasons.Add($"high-risk ownership area requires operator approval: {path.Area} {path.Path}");
        }

        foreach (var resource in resources)
        {
            reasons.Add($"reserved write-set resource: {resource}");
        }

        return new RepositoryWriteSetGuardReport(
            paths,
            resources,
            paths.Any(path => path.IsHighRisk || path.IsGeneratedOrNoisy),
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static RepositoryOwnedPath Build(
        string path,
        RepositoryOwnershipArea area,
        string reservationKey,
        bool highRisk,
        bool serialize = true)
    {
        var generated = area == RepositoryOwnershipArea.GeneratedOrNoisy;
        return new RepositoryOwnedPath(
            path,
            area,
            reservationKey,
            generated,
            highRisk,
            serialize);
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

    private static string? TestProjectReservationKey(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("tests", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var project = parts[1];
        return string.IsNullOrWhiteSpace(project) ||
               project is "." or ".." ||
               (parts.Length == 2 && project.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            ? null
            : $"{TestProjectReservationKeyPrefix}{project.ToLowerInvariant()}";
    }

    private static string SharedInfrastructureReservationKeyFor(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !parts[0].Equals("src", StringComparison.OrdinalIgnoreCase) ||
            !SharedInfrastructureProjects.TryGetValue(parts[1], out var project))
        {
            return SharedInfrastructureReservationKey;
        }

        var subsystem = parts[2];
        if (string.IsNullOrWhiteSpace(subsystem) ||
            subsystem is "." or ".." ||
            !project.Subsystems.Contains(subsystem, StringComparer.OrdinalIgnoreCase))
        {
            return SharedInfrastructureReservationKey;
        }

        return $"{SharedInfrastructureReservationKey}:{project.Token}/{subsystem.ToLowerInvariant()}";
    }

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

    private static string OwnerKeyForSource(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"source:{parts[1]}" : "source";
    }

    private static string OwnerKeyForUnknown(string path)
    {
        var first = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? "unknown" : $"unknown:{first}";
    }

    private static bool HasSegment(string path, IReadOnlyList<string> segments)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => segments.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('/');
}
