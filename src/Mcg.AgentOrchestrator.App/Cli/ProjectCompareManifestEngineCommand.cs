using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectCompareManifestEngineCommand
{
    internal const string Usage = "Usage: project compare-manifest-engine [name] [--root <path>] --manifest <path>";

    internal static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry,
        string defaultRootDirectory, string? activeProjectOverride, TextWriter output)
    {
        var (rootOverride, manifestPath) = ParseOptions(parts);
        var snapshot = TryReadLearnedSettings(parts, registry, defaultRootDirectory, activeProjectOverride, rootOverride);
        var learned = snapshot?.Settings;
        AppliedOwnerAnswers? applied = null;
        string? answerError = null;
        if (snapshot is { } stored)
        {
            var read = ProjectAnswerCommand.ReadAnswersForOutput(stored.Directory);
            answerError = read.Error;
            if (read.Answers.Count > 0)
            {
                applied = OwnerAnswerApplier.Apply(stored.Model, read.Answers);
                learned = OwnerAnsweredEngineSettings.Overlay(stored.Settings, applied);
            }
        }
        IReadOnlyList<ManifestFactComparison> comparisons;
        try
        {
            var manifestJson = File.ReadAllText(Path.GetFullPath(manifestPath));
            comparisons = ManifestEngineSettingsComparer.Compare(manifestJson, learned);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or JsonException or InvalidDataException)
        {
            throw new ArgumentException($"{Usage}{Environment.NewLine}Manifest could not be read: {exception.Message}", exception);
        }

        foreach (var comparison in comparisons)
            output.WriteLine($"{comparison.Kind} {comparison.Fact} hand={comparison.Hand} learned={comparison.Learned} ({comparison.Reason})");
        if (learned is not null)
            foreach (var question in learned.OwnerQuestions)
                output.WriteLine($"OwnerQuestion {question.FactKey} {question.Question}");
        if (applied is not null)
            ProjectAnswerCommand.WriteAnswerLines(output, applied);
        if (answerError is not null)
            output.WriteLine($"Owner answers unreadable: {answerError}");
        return comparisons.Any(comparison => comparison.Kind is ManifestFactResultKind.DiffersFromGateDefault
            or ManifestFactResultKind.DiffersFromHand) ? 1 : 0;
    }

    private static (LearnedEngineSettings Settings, ProjectModel Model, string Directory)? TryReadLearnedSettings(IReadOnlyList<string> parts,
        OrchestratorProjectRegistry registry, string defaultRootDirectory, string? activeProjectOverride, string? rootOverride)
    {
        try
        {
            var name = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal)
                ? OrchestratorProjectSelection.NormalizeProjectName(parts[2]) : null;
            var activeName = name is null ? activeProjectOverride ?? registry.ReadSelectedProjectName() : null;
            if (name is null && string.IsNullOrWhiteSpace(activeName)) return null;
            var project = name is null ? registry.ResolveActiveProject(defaultRootDirectory, activeName)
                : name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
                    ? new OrchestratorProject(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory))
                    : registry.GetRequiredProject(name);
            var directory = ProjectDiscoveryModelLocation.ResolveDirectory(project, rootOverride, defaultRootDirectory,
                registry.DataRootDirectory);
            var modelPath = Path.Combine(directory, "project-model.json");
            if (!File.Exists(modelPath)) return null;
            var model = ProjectModelJson.Deserialize(File.ReadAllText(modelPath));
            var learned = LearnedEngineSettingsDeriver.Derive(model);
            return (learned with
            {
                MaxConcurrentShardsReason = $"learned from project-model.json: {learned.MaxConcurrentShardsReason}",
                DefaultTimeoutReason = $"learned from project-model.json: {learned.DefaultTimeoutReason}"
            }, model, directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or JsonException or InvalidOperationException)
        {
            // An unresolved project or unreadable snapshot preserves the original gate-default report.
            return null;
        }
    }

    private static (string? Root, string Manifest) ParseOptions(IReadOnlyList<string> parts)
    {
        string? root = null;
        string? manifest = null;
        var index = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal) ? 3 : 2;
        for (; index < parts.Count; index++)
        {
            var option = parts[index];
            var equals = option.IndexOf('=');
            var flag = equals < 0 ? option : option[..equals];
            if (flag is not "--root" and not "--manifest")
                throw new ArgumentException(Usage);
            var value = equals < 0 ? ++index < parts.Count ? parts[index] : null : option[(equals + 1)..];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            if (flag == "--root" && root is null)
                root = value;
            else if (flag == "--manifest" && manifest is null)
                manifest = value;
            else
                throw new ArgumentException(Usage);
        }
        return (root, manifest ?? throw new ArgumentException(Usage));
    }
}
