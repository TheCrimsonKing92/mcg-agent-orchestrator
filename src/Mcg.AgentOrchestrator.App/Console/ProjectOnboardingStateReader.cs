using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Observation only: persisted evidence is never repaired or rediscovered here.
internal static class ProjectOnboardingStateReader
{
    internal static ProjectOnboardingState Read(string name, string directory, string? conductEventsLogPath = null,
        bool deliveryAnswered = false)
    {
        ProjectModel? model = null;
        IReadOnlyList<ProjectOwnerQuestion> open = [];
        IReadOnlyList<AppliedOwnerAnswers.StaleAnswer> stale = [];
        var step = ProjectOnboardingStep.Discovered;
        try
        {
            string json;
            try { json = File.ReadAllText(Path.Combine(directory, "project-model.json")); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            { return State(step); }
            model = ProjectModelJson.Deserialize(json);
            step = ProjectOnboardingStep.QuestionsAnswered;
            var applied = OwnerAnswerApplier.Apply(model, OwnerAnswerFile.Read(directory));
            open = applied.OpenQuestions.Where(question => !deliveryAnswered || question.FactKey != ProjectDelivery.ModeKey).ToArray();
            stale = applied.StaleAnswers;
            if (open.Count > 0) return State(step);

            step = ProjectOnboardingStep.ManifestInstalled;
            string manifest;
            try { manifest = File.ReadAllText(Path.Combine(directory, "acceptance-manifest.json")); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            { return State(step); }
            using var document = JsonDocument.Parse(manifest);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Installed acceptance manifest must be a JSON object.");
            // Use the gate's existing format, without deriving or comparing a new manifest.
            _ = document.RootElement.Deserialize<GoalAcceptanceVerifier.AcceptanceManifest>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            step = ProjectOnboardingStep.ConductorStarted;
            if (new ConductorLeaseLiveness(directory).IsRunning() || HasConductorHistory(conductEventsLogPath))
                return State(null);
            return State(step);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return State(step, $"{name}: {exception.Message}"); }

        ProjectOnboardingState State(ProjectOnboardingStep? current, string? error = null) =>
            new(name, current, StackText(model), open, stale, model, error);
    }

    private static string StackText(ProjectModel? model)
    {
        if (model is null) return "stack unknown";
        if (model.Stack == ProjectStack.Crosswalk)
            return $"{model.EnvironmentNeeds.FirstOrDefault(need => need.Name == "crosswalk-stack")?.Value.Value ?? "stack unknown"} via crosswalk";
        // Stack is the persisted detector selection; no inference from repository files.
        return model.Stack.ToString().ToLowerInvariant();
    }

    private static bool HasConductorHistory(string? path)
    {
        if (path is null) return false;
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return false;
        var files = Directory.EnumerateFiles(directory,
            Path.GetFileNameWithoutExtension(path) + "-*" + Path.GetExtension(path)).Prepend(path);
        foreach (var file in files)
        {
            FileStream stream;
            try { stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { continue; }
            using (stream)
            using (var reader = new StreamReader(stream))
                while (reader.ReadLine() is { } line)
                    if (ConductEventFileSource.TryParse(line, out var item) &&
                        item!.EventKind is "loop-start" or "startup-first-tick" or "tick" or "loop-stop") return true;
        }
        return false;
    }
}
