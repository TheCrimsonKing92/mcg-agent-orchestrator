using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Compatibility is invocation scoped. A base SHA is provenance, not a build-input identity.
internal sealed record DotnetBaseBuildCacheIdentity(string Fingerprint);

internal static class DotnetBaseBuildCacheIdentityResolver
{
    private const string Properties = "Configuration,TargetFramework,TargetFrameworks,RuntimeIdentifier,NETCoreSdkVersion,MSBuildSDKsPath,ProjectAssetsFile,MSBuildAllProjects";
    private const string Items = "Compile,Analyzer,AdditionalFiles,EditorConfigFiles,GlobalAnalyzerConfigFiles,AnalyzerConfigFiles,EmbeddedResource,Content,None,ProjectReference,Reference";

    internal static async Task<DotnetBaseBuildCacheIdentity?> ResolveAsync(
        string worktreePath, IReadOnlyList<string> projects, string[] buildArguments,
        DotnetBuildEnvironment environment,
        Func<string[], Task<GoalAcceptanceVerifier.CommandResult>> run,
        IReadOnlyDictionary<string, string>? processEnvironment = null)
    {
        try
        {
            var properties = EvaluationArguments(buildArguments, environment.Arguments);
            if (properties is null || projects.Count == 0) return null;
            var root = Path.GetFullPath(worktreePath);
            var directoryHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<string> { "base-build-inputs-v1", root, Normalize(JsonSerializer.Serialize(properties)),
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() };
            // Environment variables become MSBuild properties. Hash all of them rather than a
            // hand-maintained allow-list of possible build-affecting variables. Never log their values.
            var variables = processEnvironment ?? Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
            parts.Add(JsonSerializer.Serialize(variables.OrderBy(entry => entry.Key, StringComparer.Ordinal)));
            foreach (var project in projects)
            {
                var projectPath = Path.GetFullPath(project, root);
                if (!File.Exists(projectPath)) return null;
                var restore = await run(["dotnet", "msbuild", projectPath, .. properties, "-t:Restore", "-nologo", "-v:q"]).ConfigureAwait(false);
                if (!Completed(restore)) return null;
                var evaluation = await run(["dotnet", "msbuild", projectPath, .. properties,
                    $"-getProperty:{Properties}", $"-getItem:{Items}", "-nologo"]).ConfigureAwait(false);
                if (!Completed(evaluation)) return null;
                using var document = JsonDocument.Parse(evaluation.Output);
                var snapshot = document.RootElement;
                var values = snapshot.GetProperty("Properties");
                var evaluatedItems = snapshot.GetProperty("Items");
                if (Items.Split(',').Any(name => !evaluatedItems.TryGetProperty(name, out _))) return null;
                var tfm = values.GetProperty("TargetFramework").GetString();
                if (string.IsNullOrWhiteSpace(tfm) || !string.IsNullOrEmpty(values.GetProperty("TargetFrameworks").GetString())) return null;
                var sdkVersion = values.GetProperty("NETCoreSdkVersion").GetString();
                var sdkDirectory = Path.GetDirectoryName(values.GetProperty("MSBuildSDKsPath").GetString());
                if (string.IsNullOrWhiteSpace(sdkVersion) || sdkDirectory is null || !Directory.Exists(sdkDirectory)) return null;
                parts.Add(project);
                parts.Add(Normalize(CanonicalSnapshot(snapshot)));
                parts.Add(HashTree(sdkDirectory));
                // Framework references and SDK analyzers need content identity as well as version.
                var packs = Path.GetFullPath(Path.Combine(sdkDirectory, "..", "..", "packs"));
                if (!Directory.Exists(packs)) return null;
                parts.Add(HashTree(packs));

                var assetsPath = values.GetProperty("ProjectAssetsFile").GetString();
                if (assetsPath is null || !File.Exists(assetsPath)) return null;
                using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
                parts.Add(Normalize(assets.RootElement.GetRawText()));
                var packageFolders = assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(folder => folder.Name).ToArray();
                foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
                {
                    if (library.Value.GetProperty("type").GetString() != "package") continue;
                    var packagePath = library.Value.GetProperty("path").GetString();
                    var folder = packageFolders.Select(parent => Path.Combine(parent, packagePath!)).FirstOrDefault(Directory.Exists);
                    if (folder is null) return null;
                    parts.Add(HashTree(folder));
                }

                // Preprocessing includes evaluated imports, including NuGet and ancestor props/targets.
                // It also binds target/analyzer options that are not members of our property query.
                Directory.CreateDirectory(environment.RootPath);
                var preprocessedPath = Path.Combine(environment.RootPath, $"base-cache-input-{Guid.NewGuid():N}.xml");
                try
                {
                    var preprocess = await run(["dotnet", "msbuild", projectPath, .. properties,
                        $"-preprocess:{preprocessedPath}", "-nologo"]).ConfigureAwait(false);
                    if (!Completed(preprocess) || !File.Exists(preprocessedPath)) return null;
                    parts.Add(Normalize(File.ReadAllText(preprocessedPath)));
                }
                finally
                {
                    if (File.Exists(preprocessedPath)) File.Delete(preprocessedPath);
                }
                foreach (var itemGroup in evaluatedItems.EnumerateObject())
                foreach (var item in itemGroup.Value.EnumerateArray())
                {
                    if (!item.TryGetProperty("FullPath", out var fullPath)) return null;
                    var input = fullPath.GetString();
                    // References can be assembly names; unsupported resolution must not authorize reuse.
                    if (string.IsNullOrWhiteSpace(input)) return null;
                    parts.Add(Normalize(input));
                    // Roslyn discovers candidate ancestor .globalconfig paths, then passes only
                    // existing files to the compiler. Bind absence so creating one invalidates reuse.
                    if (!File.Exists(input))
                    {
                        if (itemGroup.Name != "GlobalAnalyzerConfigFiles") return null;
                        parts.Add("absent-global-analyzer-config");
                        continue;
                    }
                    parts.Add(HashFile(input));
                }
                // Include project-local generator/custom-target inputs, not just Compile items.
                parts.Add(HashTree(Path.GetDirectoryName(projectPath)!, projectInputs: true));
                for (var ancestor = Path.GetDirectoryName(projectPath); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                {
                    foreach (var file in Directory.EnumerateFiles(ancestor).Where(IsSharedInput).Order(StringComparer.Ordinal))
                    {
                        parts.Add(file);
                        parts.Add(HashFile(file));
                    }
                    if (ancestor.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                }
            }
            return new DotnetBaseBuildCacheIdentity(HashText(JsonSerializer.Serialize(parts)));

            string Normalize(string value) => value
                .Replace(environment.ArtifactsPath.Replace('\\', '/'), "<artifacts>", StringComparison.OrdinalIgnoreCase)
                .Replace(environment.ArtifactsPath, "<artifacts>", StringComparison.OrdinalIgnoreCase)
                .Replace(environment.ArtifactsPath.Replace("\\", "\\\\", StringComparison.Ordinal), "<artifacts>", StringComparison.OrdinalIgnoreCase);

            string HashTree(string directory, bool projectInputs = false)
            {
                if (directoryHashes.TryGetValue(directory, out var existing)) return existing;
                var files = EnumerateInputs(directory, projectInputs).OrderBy(file => Path.GetRelativePath(directory, file), StringComparer.Ordinal).ToArray();
                var inputs = files.Select(file => new[] { Path.GetRelativePath(directory, file), HashFile(file) });
                return directoryHashes[directory] = HashText(JsonSerializer.Serialize(inputs));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            // A failed evaluation or unreadable input is a normal cache miss, not weaker identity.
            return null;
        }
    }

    private static IEnumerable<string> EnumerateInputs(string root, bool projectInputs)
    {
        foreach (var file in Directory.EnumerateFiles(root)) yield return file;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (projectInputs && (name is "bin" or "obj" or ".git" or ".scratch" or "artifacts" or "TestResults" ||
                name.StartsWith(".orchestrator", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var file in EnumerateInputs(directory, projectInputs)) yield return file;
        }
    }

    private static bool IsSharedInput(string file) => Path.GetExtension(file).ToLowerInvariant() is
        ".props" or ".targets" or ".rsp" or ".json" or ".config" or ".editorconfig" || Path.GetFileName(file) == ".editorconfig";

    private static string CanonicalSnapshot(JsonElement snapshot)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(snapshot, writer);
        return Encoding.UTF8.GetString(stream.ToArray());

        static void Write(JsonElement element, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    // MSBuild supplies these filesystem timestamps on every evaluated item.
                    // Content hashes below bind the files; access-time changes are not inputs.
                    if (property.Name is "ModifiedTime" or "CreatedTime" or "AccessedTime") continue;
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(item, writer);
                writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }
    }

    private static string HashFile(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static bool Completed(GoalAcceptanceVerifier.CommandResult result) =>
        result.ExitCode == 0 && !result.TimedOut && !result.CaptureLimited;

    // Evaluate the same global properties as the build. Unknown command shapes safely opt out.
    internal static string[]? EvaluationArguments(string[] buildArguments, IReadOnlyList<string> environmentArguments)
    {
        var result = new List<string>();
        var arguments = buildArguments.Skip(3).Concat(environmentArguments).ToArray();
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            var property = argument switch
            {
                "-c" or "--configuration" => "Configuration",
                "-f" or "--framework" => "TargetFramework",
                "-r" or "--runtime" => "RuntimeIdentifier",
                "--artifacts-path" => "ArtifactsPath",
                _ => null
            };
            if (property is not null)
            {
                if (++index >= arguments.Length) return null;
                result.Add($"-p:{property}={arguments[index]}");
            }
            else if (argument.StartsWith("-p:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("/p:", StringComparison.OrdinalIgnoreCase)) result.Add(argument);
            else if (argument is "-v" or "--verbosity") index++;
            else if (argument is "--nologo" || argument.StartsWith("-maxcpucount", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-v:", StringComparison.OrdinalIgnoreCase)) { }
            else return null;
        }
        return [.. result];
    }
}
