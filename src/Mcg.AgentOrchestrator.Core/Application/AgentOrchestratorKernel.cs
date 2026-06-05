namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private readonly Dictionary<GoalId, Goal> _goals = [];
    private readonly Dictionary<HumanInputRequestId, HumanInputRequest> _humanInputRequests = [];
    private readonly IClock _clock;

    public AgentOrchestratorKernel(IClock? clock = null)
    {
        _clock = clock ?? new SystemClock();
    }

    public IReadOnlyCollection<Goal> Goals => _goals.Values;

    public IReadOnlyCollection<HumanInputRequest> HumanInputRequests => _humanInputRequests.Values;

    public OrchestratorSnapshot ExportSnapshot()
    {
        return new OrchestratorSnapshot(
            _goals.Values.Select(goal => goal.ToSnapshot()).ToList(),
            _humanInputRequests.Values.Select(request => request.ToSnapshot()).ToList());
    }

    public static AgentOrchestratorKernel FromSnapshot(OrchestratorSnapshot snapshot, IClock? clock = null)
    {
        var kernel = new AgentOrchestratorKernel(clock);

        foreach (var goal in snapshot.Goals.Select(Goal.FromSnapshot))
        {
            kernel._goals.Add(goal.Id, goal);
        }

        foreach (var request in snapshot.HumanInputRequests.Select(HumanInputRequest.FromSnapshot))
        {
            kernel._humanInputRequests.Add(request.Id, request);
        }

        return kernel;
    }





}