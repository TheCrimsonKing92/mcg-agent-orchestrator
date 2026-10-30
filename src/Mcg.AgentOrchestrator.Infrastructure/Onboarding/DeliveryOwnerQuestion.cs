using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class DeliveryOwnerQuestion
{
    public static ProjectModel Apply(ProjectModel model, PullRequestRequirement probe, string branch, bool hasDeliveryAnswer)
    {
        var questions = model.OwnerQuestions.Where(question => question.FactKey != ProjectDelivery.ModeKey).ToList();
        if (probe.Kind == PullRequestRequirementKind.Required && !hasDeliveryAnswer)
            questions.Add(new ProjectOwnerQuestion(ProjectDelivery.ModeKey,
                $"The {branch} branch requires pull requests. Deliver by opening a pull request instead of pushing to {branch}?",
                new FactSource(measurementReference: "forge:github/pull-request-requirement"))
            {
                SuggestedValue = ProjectDelivery.PullRequest
            });
        return model with
        {
            DeliveryProbe = new DeliveryProbeRecord(probe.Kind.ToString(), probe.Reason),
            OwnerQuestions = questions.OrderBy(question => question.FactKey, StringComparer.Ordinal).ToArray()
        };
    }
}
