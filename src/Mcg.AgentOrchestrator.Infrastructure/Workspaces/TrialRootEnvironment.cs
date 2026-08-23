namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class TrialRootEnvironment
{
    internal static readonly IReadOnlyList<string> RootLocalVariables =
    [
        "McgIsolatedArtifactsPath",
        "DOTNET_CLI_HOME",
        "NUGET_PACKAGES",
        "NUGET_HTTP_CACHE_PATH",
        "NUGET_SCRATCH",
        "NUGET_PLUGINS_CACHE_PATH",
        "TEMP",
        "TMP",
        "TMPDIR",
        "MCG_TRIAL_HARNESS_STATE",
        "USERPROFILE",
        "HOME",
        "APPDATA",
        "LOCALAPPDATA"
    ];

    private static readonly string[] ReadOnlyInheritedVariables =
    [
        "COMSPEC",
        "OS",
        "PATH",
        "PATHEXT",
        "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_IDENTIFIER",
        "PROCESSOR_LEVEL",
        "PROCESSOR_REVISION",
        "SystemDrive",
        "SystemRoot",
        "WINDIR"
    ];

    public static IReadOnlyDictionary<string, string?> Build(
        string rootPath,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        var stateRoot = Path.Combine(rootPath, ".trial-state");
        var profile = Path.Combine(stateRoot, "profile");
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["McgIsolatedArtifactsPath"] = Path.Combine(stateRoot, "artifacts"),
            ["DOTNET_CLI_HOME"] = Path.Combine(stateRoot, "dotnet-home"),
            ["NUGET_PACKAGES"] = Path.Combine(stateRoot, "nuget", "packages"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(stateRoot, "nuget", "http-cache"),
            ["NUGET_SCRATCH"] = Path.Combine(stateRoot, "nuget", "scratch"),
            ["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(stateRoot, "nuget", "plugins-cache"),
            ["TEMP"] = Path.Combine(stateRoot, "temp"),
            ["TMP"] = Path.Combine(stateRoot, "temp"),
            ["TMPDIR"] = Path.Combine(stateRoot, "temp"),
            ["MCG_TRIAL_HARNESS_STATE"] = Path.Combine(stateRoot, "harness"),
            ["USERPROFILE"] = profile,
            ["HOME"] = profile,
            ["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local")
        };

        foreach (var name in ReadOnlyInheritedVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value;
            }
        }

        if (extraEnvironment is not null)
        {
            foreach (var pair in extraEnvironment)
            {
                values[pair.Key] = pair.Value;
            }
        }

        foreach (var name in RootLocalVariables)
        {
            var path = Path.GetFullPath(values[name] ?? throw new InvalidOperationException(
                $"Trial environment variable '{name}' has no value."));
            if (!IsBelow(rootPath, path))
            {
                throw new InvalidOperationException(
                    $"Trial environment variable '{name}' must resolve beneath the trial root.");
            }

            Directory.CreateDirectory(path);
            values[name] = path;
        }

        return values;
    }

    internal static bool IsBelow(string rootPath, string candidatePath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
