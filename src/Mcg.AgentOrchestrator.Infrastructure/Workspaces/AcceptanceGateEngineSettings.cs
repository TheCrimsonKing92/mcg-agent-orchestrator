using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class AcceptanceGateEngineSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IReadOnlyList<AcceptanceTestLane> InfrastructureTestLanes { get; init; } = [];
    public IReadOnlyList<AcceptanceMtpInvocation> MtpInvocations { get; init; } = [];
    public AcceptanceGateTimeoutSettings Timeouts { get; init; } = new();
    public int MaxConcurrentShards { get; init; } = 1;
    public bool EnforceStructuralCoverage { get; init; }

    public static AcceptanceGateEngineSettings Load(string worktreePath)
    {
        var manifestPath = ResolveManifestPath(worktreePath);
        if (!File.Exists(manifestPath))
        {
            return new AcceptanceGateEngineSettings();
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("engine", out var engineElement))
        {
            return new AcceptanceGateEngineSettings();
        }

        var settings = engineElement.Deserialize<AcceptanceGateEngineSettings>(JsonOptions)
            ?? throw new InvalidDataException("Acceptance manifest engine settings could not be read.");
        settings.Validate();
        return settings;
    }

    public AcceptanceMtpInvocation ResolveMtpInvocation(string project)
    {
        var normalizedProject = NormalizePath(project);
        return MtpInvocations.SingleOrDefault(invocation =>
                NormalizePath(invocation.Project).Equals(normalizedProject, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Acceptance manifest has no MTP invocation for project '{project}'.");
    }

    public bool HasMtpInvocation(string project)
    {
        var normalizedProject = NormalizePath(project);
        return MtpInvocations.Any(invocation =>
            NormalizePath(invocation.Project).Equals(normalizedProject, StringComparison.OrdinalIgnoreCase));
    }

    public TimeSpan ResolveCheckTimeout(int? timeoutMinutes) =>
        timeoutMinutes is > 0
            ? TimeSpan.FromMinutes(timeoutMinutes.Value)
            : AcceptanceCheckTimeouts.Resolve(Timeouts.DefaultMinutes);

    public TimeSpan ResolveBuildServerShutdownTimeout() =>
        AcceptanceCheckTimeouts.Resolve(Timeouts.BuildServerShutdownMinutes ?? Timeouts.DefaultMinutes);

    public TimeSpan ResolveDiscoveryTimeout() =>
        AcceptanceCheckTimeouts.Resolve(Timeouts.DiscoveryMinutes ?? Timeouts.DefaultMinutes);

    private void Validate()
    {
        if (MaxConcurrentShards < 1)
        {
            throw new InvalidDataException("Acceptance manifest engine maxConcurrentShards must be at least 1.");
        }

        var duplicateLane = InfrastructureTestLanes
            .GroupBy(lane => lane.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateLane is not null)
        {
            throw new InvalidDataException($"Acceptance manifest test lane name '{duplicateLane.Key}' is duplicated.");
        }

        foreach (var lane in InfrastructureTestLanes)
        {
            if (string.IsNullOrWhiteSpace(lane.Name) || string.IsNullOrWhiteSpace(lane.Filter))
            {
                throw new InvalidDataException("Acceptance manifest test lanes require non-empty names and filters.");
            }
        }

        foreach (var invocation in MtpInvocations)
        {
            invocation.Validate();
        }
    }

    private static string ResolveManifestPath(string worktreePath)
    {
        var trackedPath = Path.Combine(worktreePath, "config", "acceptance-manifest.json");
        return File.Exists(trackedPath)
            ? trackedPath
            : Path.Combine(worktreePath, ".orchestrator", "acceptance-manifest.json");
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}

internal sealed class AcceptanceGateTimeoutSettings
{
    public int? DefaultMinutes { get; init; }
    public int? BuildServerShutdownMinutes { get; init; }
    public int? DiscoveryMinutes { get; init; }
}

internal sealed record AcceptanceTestLane(string Name, string Filter);

internal sealed class AcceptanceMtpInvocation
{
    public string Project { get; init; } = string.Empty;
    public string ExecutablePathTemplate { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string ResolveExecutablePath(DotnetBuildEnvironment environment)
    {
        var projectName = Path.GetFileNameWithoutExtension(Project);
        var relativePath = RenderTemplate(ExecutablePathTemplate, projectName);
        var candidate = Path.GetFullPath(Path.Combine(environment.ArtifactsPath, relativePath));
        var artifactsRoot = Path.GetFullPath(environment.ArtifactsPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(artifactsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"MTP executable path for '{Project}' escapes the trusted artifacts root.");
        }

        return candidate;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Project) ||
            string.IsNullOrWhiteSpace(ExecutablePathTemplate))
        {
            throw new InvalidDataException(
                "Acceptance manifest MTP invocations require project and executablePathTemplate.");
        }

        if (Arguments.Count == 0 ||
            !Arguments[0].Equals("{executable}", StringComparison.Ordinal) ||
            Arguments.Skip(1).Contains("{executable}", StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Acceptance manifest MTP invocation for '{Project}' must use '{{executable}}' exactly once as its first argument.");
        }
    }

    private static string RenderTemplate(string template, string projectName) =>
        template
            .Replace("{projectName}", projectName, StringComparison.Ordinal)
            .Replace("{configuration}", "debug", StringComparison.Ordinal)
            .Replace("{executableExtension}", OperatingSystem.IsWindows() ? ".exe" : string.Empty, StringComparison.Ordinal);
}
