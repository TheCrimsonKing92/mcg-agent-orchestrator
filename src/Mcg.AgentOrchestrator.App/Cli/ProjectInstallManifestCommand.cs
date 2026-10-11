using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectInstallManifestCommand
{
    internal const string Usage = "Usage: project install-manifest [name] --from <path>";
    internal static Action? ReplaceFaultForTests { get; set; }
    internal static Func<DateTimeOffset>? UtcNowForTests { get; set; }

    internal static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry,
        string defaultRootDirectory, string? activeProjectOverride, TextWriter output)
    {
        var (name, source) = ParseOptions(parts);
        OrchestratorProject project;
        try
        {
            name = name is null ? null : OrchestratorProjectSelection.NormalizeProjectName(name);
            project = name is null ? registry.ResolveActiveProject(defaultRootDirectory, activeProjectOverride)
                : name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
                    ? new OrchestratorProject(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory))
                    : registry.GetRequiredProject(name);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"{Usage}{Environment.NewLine}{exception.Message}", exception);
        }

        if (project.Name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase))
            return Refuse("the default home project has no installable live manifest");
        if (!project.ResolveWorkspace().IsProjectScoped)
            return Refuse("the workspace is not project-scoped");
        var directory = ProjectDiscoveryModelLocation.ResolveDirectory(project, null, defaultRootDirectory,
            registry.DataRootDirectory);
        var live = Path.Combine(directory, "acceptance-manifest.json");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootDirectory));
        if (live.Equals(root, StringComparison.OrdinalIgnoreCase)
            || live.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return Refuse("the live manifest is inside the project source root");
        if (!File.Exists(source)) return Refuse($"--from file does not exist {source}");
        if (Path.TrimEndingDirectorySeparator(source).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(live)), StringComparison.OrdinalIgnoreCase))
            return Refuse($"--from is the live manifest {live}");

        var modelPath = Path.Combine(directory, "project-model.json");
        if (!File.Exists(modelPath))
            return Refuse($"Missing project model {modelPath}; run project discover.");
        ProjectModel model;
        try
        {
            model = ProjectModelJson.Deserialize(File.ReadAllText(modelPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or JsonException or InvalidOperationException or InvalidDataException)
        {
            return Refuse($"Project model unreadable: {exception.Message}; run project discover.");
        }
        var (answers, error) = ProjectAnswerCommand.ReadAnswersForOutput(directory);
        if (error is not null)
        {
            output.WriteLine($"Owner answers unreadable: {error}");
            return 1;
        }
        var result = LearnedAcceptanceManifest.Build(model, answers);
        if (result.Blockers.Count > 0)
        {
            output.WriteLine($"Refused: {result.Blockers.Count} facts the gate needs are unanswered");
            foreach (var blocker in result.Blockers)
                output.WriteLine($"Blocked {blocker.FactKey} {blocker.Reason}");
            foreach (var question in result.OwnerQuestions)
                output.WriteLine($"OwnerQuestion {question.FactKey} {question.Question}");
            return 1;
        }

        var expected = new UTF8Encoding(false).GetBytes(result.Json!);
        byte[] actual;
        try
        {
            actual = File.ReadAllBytes(source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refuse($"--from file unreadable {source}: {exception.Message}");
        }
        var offset = 0;
        while (offset < Math.Min(actual.Length, expected.Length) && actual[offset] == expected[offset]) offset++;
        if (offset < Math.Max(actual.Length, expected.Length))
            return Refuse($"--from differs from the current learned manifest at byte offset {offset}");

        string? backup = null;
        var temporary = Path.Combine(directory, $".acceptance-manifest.json-{Guid.NewGuid():N}.tmp");
        try
        {
            if (File.Exists(live))
            {
                var stamp = (UtcNowForTests?.Invoke() ?? DateTimeOffset.UtcNow).UtcDateTime
                    .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                backup = live + ".bak-" + stamp;
                if (File.Exists(backup) || Directory.Exists(backup))
                    return Refuse($"backup already exists {backup}");
                File.Copy(live, backup, overwrite: false);
            }
            File.WriteAllBytes(temporary, expected);
            ReplaceFaultForTests?.Invoke();
            File.Move(temporary, live, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refuse($"could not install {live}: {exception.Message}");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        var previous = backup is null ? "no previous manifest" : $"previous saved as {backup}";
        output.WriteLine($"Installed manifest: {live} ({LearnedTestCheckDeriver.Derive(model).Checks.Count} checks; {previous})");
        return 0;

        int Refuse(string reason)
        {
            output.WriteLine($"Refused: {reason}");
            return 1;
        }
    }

    private static (string? Name, string Source) ParseOptions(IReadOnlyList<string> parts)
    {
        string? name = null;
        string? source = null;
        for (var index = 2; index < parts.Count; index++)
        {
            var argument = parts[index];
            if (!argument.StartsWith("-", StringComparison.Ordinal))
            {
                if (name is not null || string.IsNullOrWhiteSpace(argument)) throw new ArgumentException(Usage);
                name = argument;
                continue;
            }
            var equals = argument.IndexOf('=');
            var flag = equals < 0 ? argument : argument[..equals];
            if (flag != "--from" || source is not null) throw new ArgumentException(Usage);
            source = equals < 0 ? ++index < parts.Count ? parts[index] : null : argument[(equals + 1)..];
            if (string.IsNullOrWhiteSpace(source) || source.StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
        }
        if (source is null) throw new ArgumentException(Usage);
        try
        {
            return (name, Path.GetFullPath(source));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(Usage, exception);
        }
    }
}
