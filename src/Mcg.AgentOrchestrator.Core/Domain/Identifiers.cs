namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalId(string Value)
{
    public static GoalId New() => new(Guid.NewGuid().ToString("n"));
    public override string ToString() => Value;
}

public sealed record TaskId(string Value)
{
    public static TaskId New() => new(Guid.NewGuid().ToString("n"));
    public override string ToString() => Value;
}

public sealed record AgentId(string Value)
{
    public static AgentId New() => new(Guid.NewGuid().ToString("n"));
    public override string ToString() => Value;
}

public sealed record HumanInputRequestId(string Value)
{
    public static HumanInputRequestId New() => new(Guid.NewGuid().ToString("n"));
    public override string ToString() => Value;
}
