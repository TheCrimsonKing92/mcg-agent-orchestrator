namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private IReadOnlyList<EngineeringPractice> _engineeringPractices = EngineeringPracticeDefaults.SeedEntries;

    public IReadOnlyList<EngineeringPractice> EngineeringPractices => _engineeringPractices;

    public void SetEngineeringPractices(IEnumerable<EngineeringPractice> practices)
    {
        ArgumentNullException.ThrowIfNull(practices);
        _engineeringPractices = practices
            .Where(practice => !string.IsNullOrWhiteSpace(practice.Id))
            .GroupBy(practice => practice.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    public IReadOnlyList<EngineeringPracticeMatch> MatchEngineeringPractices(
        Goal goal,
        TaskSpec task,
        IReadOnlyList<string>? changedFiles = null,
        string? extraScopeText = null)
    {
        return EngineeringPracticeRegistryMatcher.Match(
            _engineeringPractices,
            goal,
            task,
            changedFiles,
            extraScopeText);
    }
}
