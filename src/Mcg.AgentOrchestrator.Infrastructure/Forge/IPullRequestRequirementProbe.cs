namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IPullRequestRequirementProbe
{
    PullRequestRequirement Probe(string repositoryRoot, string branch);
}
