namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class NullOperatorChannel : IOperatorChannel
{
    public static readonly NullOperatorChannel Instance = new();

    public string ChannelType => "null";

    public Task SendEscalationAsync(OperatorEscalation escalation, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
