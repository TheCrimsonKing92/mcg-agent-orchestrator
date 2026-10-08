using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class AcceptanceGateEngineSettings
{
    private const int DefaultPartitionVerdictFullRerunEveryN = 5;
    internal const long DefaultOutputCaptureLimitBytes = 1024L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IReadOnlyList<AcceptanceTestLane> InfrastructureTestLanes { get; init; } = [];
    public IReadOnlyList<AcceptanceMtpInvocation> MtpInvocations { get; init; } = [];
    public AcceptanceGateTimeoutSettings Timeouts { get; init; } = new();
    public int MaxConcurrentShards { get; init; } = 1;
    public bool EnforceStructuralCoverage { get; init; }
    public int PartitionVerdictFullRerunEveryN { get; init; } = DefaultPartitionVerdictFullRerunEveryN;
    public long OutputCaptureLimitBytes { get; init; } = DefaultOutputCaptureLimitBytes;

    public static AcceptanceGateEngineSettings Load(string worktreePath, string? projectHomeDirectory = null)
    {
        var manifestPath = AcceptanceManifestLocator.Resolve(worktreePath, projectHomeDirectory);
        if (!File.Exists(manifestPath))
        {
            return new AcceptanceGateEngineSettings();
        }

        return Parse(File.ReadAllText(manifestPath));
    }

    internal static AcceptanceGateEngineSettings Parse(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
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

        if (PartitionVerdictFullRerunEveryN < 1)
        {
            throw new InvalidDataException(
                "Acceptance manifest engine partitionVerdictFullRerunEveryN must be at least 1.");
        }

        if (OutputCaptureLimitBytes < 1)
        {
            throw new InvalidDataException(
                "Acceptance manifest engine outputCaptureLimitBytes must be positive.");
        }

        var duplicateLane = InfrastructureTestLanes
            .GroupBy(lane => lane.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateLane is not null)
        {
            throw new InvalidDataException($"Acceptance manifest test lane name '{duplicateLane.Key}' is duplicated.");
        }

        var collectionOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in InfrastructureTestLanes)
        {
            if (string.IsNullOrWhiteSpace(lane.Name) || string.IsNullOrWhiteSpace(lane.Filter))
            {
                throw new InvalidDataException("Acceptance manifest test lanes require non-empty names and filters.");
            }

            if (!double.IsFinite(lane.EstimatedSerialSeconds) || lane.EstimatedSerialSeconds < 0)
            {
                throw new InvalidDataException(
                    $"Acceptance manifest test lane '{lane.Name}' estimatedSerialSeconds must be a finite non-negative number.");
            }

            if (lane.ExclusiveResourceKeys is null ||
                lane.ExclusiveResourceKeys.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException(
                    $"Acceptance manifest test lane '{lane.Name}' exclusiveResourceKeys must contain only non-empty keys.");
            }

            var duplicateResourceKey = lane.ExclusiveResourceKeys
                .Select(key => key.Trim())
                .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateResourceKey is not null)
            {
                throw new InvalidDataException(
                    $"Acceptance manifest test lane '{lane.Name}' exclusive resource key '{duplicateResourceKey.Key}' is duplicated.");
            }
            if (lane.OwnedCollections is null || lane.OwnedCollections.Any(string.IsNullOrWhiteSpace) ||
                lane.OwnedCollections.Any(name => name != name.Trim()))
            {
                throw new InvalidDataException($"Acceptance manifest test lane '{lane.Name}' has an invalid owned collection.");
            }
            var translatedFilter = AcceptanceCheckCommandBuilder.TranslateMtpFilter(lane.Filter).ToArray();
            if (lane.OwnedCollections.Count > 0 &&
                !translatedFilter.Contains("--filter-class"))
            {
                throw new InvalidDataException($"Acceptance manifest test lane '{lane.Name}' must have an inclusion filter to own a collection.");
            }
            foreach (var collection in lane.OwnedCollections)
            {
                if (!collectionOwners.Add(collection))
                {
                    throw new InvalidDataException($"Acceptance manifest collection '{collection}' has multiple owners.");
                }
            }
        }

        foreach (var invocation in MtpInvocations)
        {
            invocation.Validate();
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}

internal sealed class AcceptanceGateTimeoutSettings
{
    public int? DefaultMinutes { get; init; }
    public int? BuildServerShutdownMinutes { get; init; }
    public int? DiscoveryMinutes { get; init; }
}

internal sealed record AcceptanceTestLane(
    string Name,
    string Filter,
    double EstimatedSerialSeconds = 0)
{
    public IReadOnlyList<string> ExclusiveResourceKeys { get; init; } = [];
    public IReadOnlyList<string> OwnedCollections { get; init; } = [];
    public bool RequiresBuildSystemChange { get; init; }
}

internal sealed class AcceptanceMtpInvocation
{
    public string Project { get; init; } = string.Empty;
    public string ExecutablePathTemplate { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string ResolveExecutablePath(DotnetBuildEnvironment environment)
        => ResolveArtifactPath(
            environment,
            OperatingSystem.IsWindows() ? ".exe" : string.Empty,
            "executable",
            requiredExtension: null);

    public string ResolveManagedAssemblyPath(DotnetBuildEnvironment environment)
        => ResolveArtifactPath(environment, ".dll", "managed assembly", requiredExtension: ".dll");

    public IReadOnlyList<string> ResolveRequiredBuildArtifacts(DotnetBuildEnvironment environment) =>
        [ResolveExecutablePath(environment), ResolveManagedAssemblyPath(environment)];

    private string ResolveArtifactPath(
        DotnetBuildEnvironment environment,
        string executableExtension,
        string artifactKind,
        string? requiredExtension)
    {
        var projectName = Path.GetFileNameWithoutExtension(Project);
        var relativePath = RenderTemplate(ExecutablePathTemplate, projectName, executableExtension);
        var candidate = Path.GetFullPath(Path.Combine(environment.ArtifactsPath, relativePath));
        var artifactsRoot = Path.GetFullPath(environment.ArtifactsPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(artifactsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"MTP {artifactKind} path for '{Project}' escapes the trusted artifacts root.");
        }

        if (requiredExtension is not null &&
            !candidate.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"MTP {artifactKind} path for '{Project}' must resolve to a {requiredExtension} file.");
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

        if (!ExecutablePathTemplate.Contains("{executableExtension}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Acceptance manifest MTP invocation for '{Project}' must include '{{executableExtension}}' in executablePathTemplate.");
        }

        if (Arguments.Count == 0 ||
            !Arguments[0].Equals("{executable}", StringComparison.Ordinal) ||
            Arguments.Skip(1).Contains("{executable}", StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Acceptance manifest MTP invocation for '{Project}' must use '{{executable}}' exactly once as its first argument.");
        }
    }

    private static string RenderTemplate(
        string template,
        string projectName,
        string executableExtension) =>
        template
            .Replace("{projectName}", projectName, StringComparison.Ordinal)
            .Replace("{configuration}", "debug", StringComparison.Ordinal)
            .Replace("{executableExtension}", executableExtension, StringComparison.Ordinal);
}
