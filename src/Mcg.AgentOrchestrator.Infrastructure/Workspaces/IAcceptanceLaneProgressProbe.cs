namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IAcceptanceLaneProgressProbe
{
    // Returns an extra-progress fingerprint, or null when absent. Must never throw.
    string? Sample();
}
