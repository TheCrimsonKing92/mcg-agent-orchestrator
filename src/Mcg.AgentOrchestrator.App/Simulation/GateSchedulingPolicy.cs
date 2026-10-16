namespace Mcg.AgentOrchestrator.App.Simulation;

internal sealed record GateSchedulingPolicy(int Width, int TrainSize, bool CohortsEnabled,
    GateDropRule DropRule, int Seed)
{
    internal static GateSchedulingPolicy Current { get; } = new(1, 3, true, GateDropRule.Newest, 1);

    internal void Validate()
    {
        if (Width is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(Width));
        if (TrainSize is not (0 or 3 or 4)) throw new ArgumentOutOfRangeException(nameof(TrainSize));
        if (!Enum.IsDefined(DropRule)) throw new ArgumentOutOfRangeException(nameof(DropRule));
    }
}
