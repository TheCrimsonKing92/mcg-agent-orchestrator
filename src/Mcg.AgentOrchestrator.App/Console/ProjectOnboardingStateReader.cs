using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Observation only: persisted evidence is never repaired or rediscovered here.
internal static class ProjectOnboardingStateReader
{
    internal static ProjectOnboardingState Read(string name, string directory, string? conductEventsLogPath = null,
        bool deliveryAnswered = false, bool useLearnedBranch = false, bool goalLanded = false)
    {
        ProjectModel? model = null;
        IReadOnlyList<ProjectOwnerQuestion> open = [];
        IReadOnlyList<AppliedOwnerAnswers.StaleAnswer> stale = [];
        var step = ProjectOnboardingStep.Discovered;
        var complete = ProjectOnboardingCompletion.IsComplete(directory, conductEventsLogPath, goalLanded);
        try
        {
            string json;
            try { json = File.ReadAllText(Path.Combine(directory, "project-model.json")); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            { return State(complete ? null : step); }
            model = ProjectModelJson.Deserialize(json);
            step = ProjectOnboardingStep.QuestionsAnswered;
            var applied = OwnerAnswerApplier.Apply(model, OwnerAnswerFile.Read(directory));
            stale = applied.StaleAnswers;
            if (complete) return State(null);
            open = applied.OpenQuestions.Where(question =>
                (!deliveryAnswered || question.FactKey != ProjectDelivery.ModeKey) &&
                OnboardingQuestionResolution.Classify(question.FactKey, name, useLearnedBranch).IsActionable).ToArray();
            if (open.Count > 0) return State(step);

            step = ProjectOnboardingStep.ManifestInstalled;
            if (!ProjectOnboardingCompletion.ManifestInstalled(directory)) return State(step);

            step = ProjectOnboardingStep.ConductorStarted;
            if (ProjectOnboardingCompletion.ConductorRan(directory, conductEventsLogPath))
                return State(null);
            return State(step);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return complete ? State(null) : State(step, $"{name}: {exception.Message}"); }

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
}
