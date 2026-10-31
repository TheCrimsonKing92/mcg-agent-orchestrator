using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A displayed question must have an interaction supported by the owning answer/command seam.
internal sealed record OnboardingQuestionResolution(OnboardingQuestionResolutionKind Kind, string? Command = null)
{
    internal bool IsActionable => Kind is OnboardingQuestionResolutionKind.Answer or
        OnboardingQuestionResolutionKind.RegistryAnswer or OnboardingQuestionResolutionKind.Command;

    internal static OnboardingQuestionResolution Classify(string factKey, string project, bool useLearnedBranch)
    {
        if (factKey is ProjectDelivery.ModeKey or ProjectDelivery.DeciderKey)
            return new(OnboardingQuestionResolutionKind.RegistryAnswer);
        if (factKey == LearnedIntegrationBranch.OwnerQuestionKey)
            return useLearnedBranch ? new(OnboardingQuestionResolutionKind.Resolved) :
                new(OnboardingQuestionResolutionKind.Command, $"project use-learned-branch {project}");
        return new(OwnerAnswerValidator.HasRule(factKey)
            ? OnboardingQuestionResolutionKind.Answer : OnboardingQuestionResolutionKind.Unsupported);
    }
}
