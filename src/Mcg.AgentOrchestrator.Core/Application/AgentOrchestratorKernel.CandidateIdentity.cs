namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private Func<Goal, CandidateIdentity?>? _candidateIdentityResolver;

    public void ConfigureCandidateIdentityResolver(Func<Goal, CandidateIdentity?> resolver) =>
        _candidateIdentityResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    private CandidateIdentity? ResolveCandidateIdentity(Goal goal)
    {
        try { return _candidateIdentityResolver?.Invoke(goal); }
        catch { return null; }
    }
}
