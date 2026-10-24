using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectEmitManifestCommand
{
    internal const string Usage = "Usage: project emit-manifest [name] --out <path>";

    internal static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry,
        string defaultRootDirectory, string? activeProjectOverride, TextWriter output)
    {
        var (name, destination) = ParseOptions(parts);
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

        var directory = ProjectDiscoveryModelLocation.ResolveDirectory(project, null, defaultRootDirectory,
            registry.DataRootDirectory);
        var refusal = DestinationRefusal(destination, project.RootDirectory, directory);
        if (refusal is not null)
        {
            output.WriteLine($"Refused: {refusal} {destination}");
            return 1;
        }

        var modelPath = Path.Combine(directory, "project-model.json");
        if (!File.Exists(modelPath))
        {
            output.WriteLine($"Refused: Missing project model {modelPath}; run project discover.");
            return 1;
        }
        ProjectModel model;
        try
        {
            model = ProjectModelJson.Deserialize(File.ReadAllText(modelPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or JsonException or InvalidOperationException or InvalidDataException)
        {
            output.WriteLine($"Refused: Project model unreadable: {exception.Message}; run project discover.");
            return 1;
        }
        var (answers, error) = ProjectAnswerCommand.ReadAnswersForOutput(directory);
        if (error is not null)
        {
            output.WriteLine($"Owner answers unreadable: {error}");
            return 1;
        }
        var mtpLayoutRefusals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var check in LearnedTestCheckDeriver.Derive(model, OwnerAnswerApplier.Apply(model, answers)).Checks
            .Where(check => check.Runner == "mtp"))
            if (MtpBuildLayoutReader.ReadRefusal(project.RootDirectory, check.ProjectPath) is { } reason)
                mtpLayoutRefusals[check.ProjectPath] = reason;
        var result = LearnedAcceptanceManifest.Build(model, answers, mtpLayoutRefusals);
        if (result.Blockers.Count > 0)
        {
            output.WriteLine($"Refused: {result.Blockers.Count} facts the gate needs are unanswered");
            foreach (var blocker in result.Blockers)
                output.WriteLine($"Blocked {blocker.FactKey} {blocker.Reason}");
            foreach (var question in result.OwnerQuestions)
                output.WriteLine($"OwnerQuestion {question.FactKey} {question.Question}");
            return 1;
        }

        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, result.Json!, new UTF8Encoding(false));
            File.Move(temporary, destination, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Refused: could not write {destination}: {exception.Message}");
            return 1;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        using var manifest = JsonDocument.Parse(result.Json!);
        output.WriteLine($"Emitted manifest: {destination} ({manifest.RootElement.GetProperty("checks").GetArrayLength()} checks)");
        return 0;
    }

    private static string? DestinationRefusal(string destination, string sourceRoot, string modelDirectory)
    {
        if (Path.GetFileName(destination).Equals("acceptance-manifest.json", StringComparison.OrdinalIgnoreCase))
            return "destination file name acceptance-manifest.json is reserved";
        if (IsInside(destination, sourceRoot)) return "destination is inside the project source root";
        if (IsInside(destination, modelDirectory)) return "destination is inside the model directory";
        if (File.Exists(destination) || Directory.Exists(destination)) return "destination already exists";
        if (!Directory.Exists(Path.GetDirectoryName(destination))) return "parent directory does not exist";
        return null;
    }

    private static bool IsInside(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Name, string Destination) ParseOptions(IReadOnlyList<string> parts)
    {
        string? name = null;
        string? destination = null;
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
            if (flag != "--out" || destination is not null) throw new ArgumentException(Usage);
            destination = equals < 0 ? ++index < parts.Count ? parts[index] : null : argument[(equals + 1)..];
            if (string.IsNullOrWhiteSpace(destination) || destination.StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
        }
        if (destination is null) throw new ArgumentException(Usage);
        try
        {
            return (name, Path.GetFullPath(destination));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(Usage, exception);
        }
    }
}
