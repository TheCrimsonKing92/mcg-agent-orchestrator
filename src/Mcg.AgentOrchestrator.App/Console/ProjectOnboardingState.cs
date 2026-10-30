using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// CurrentStep is the earliest incomplete prerequisite, or null when all steps are complete.
internal sealed record ProjectOnboardingState(string Project, ProjectOnboardingStep? CurrentStep,
    string Stack, IReadOnlyList<ProjectOwnerQuestion> OpenQuestions,
    IReadOnlyList<AppliedOwnerAnswers.StaleAnswer> StaleAnswers, ProjectModel? Model = null,
    string? Unavailable = null)
{
    internal bool IsOnboarding => CurrentStep is not null;
    internal int OpenQuestionCount => OpenQuestions.Count;
    internal string Progress => Unavailable is not null ? $"unavailable: {Unavailable.ReplaceLineEndings(" ")}" : CurrentStep switch
    {
        ProjectOnboardingStep.Registered => "not registered",
        ProjectOnboardingStep.Discovered => "not discovered",
        ProjectOnboardingStep.QuestionsAnswered => $"{OpenQuestionCount} question{(OpenQuestionCount == 1 ? "" : "s")} open",
        ProjectOnboardingStep.ManifestInstalled => "manifest not installed",
        ProjectOnboardingStep.ConductorStarted => "manifest installed · conductor not started",
        null => "conductor started",
        _ => throw new InvalidOperationException("Unknown onboarding step.")
    };
}
