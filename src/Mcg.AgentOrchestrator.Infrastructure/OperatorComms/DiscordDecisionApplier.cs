namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class DiscordDecisionApplier
{
    private readonly string _auditDirectory;
    private readonly Func<string, CancellationToken, Task> _dispatch;
    private readonly Action<string>? _acknowledge;
    private readonly Func<string, CancellationToken, Task>? _postResult;

    public DiscordDecisionApplier(
        string auditDirectory,
        Func<string, CancellationToken, Task> dispatch,
        Action<string>? acknowledge = null,
        Func<string, CancellationToken, Task>? postResult = null)
    {
        _auditDirectory = auditDirectory;
        _dispatch = dispatch;
        _acknowledge = acknowledge;
        _postResult = postResult;
    }

    public async Task<bool> ApplyAsync(OperatorDecision decision, CancellationToken cancellationToken = default)
    {
        if (!OperatorDecisionLog.TryRecord(_auditDirectory, decision, DateTimeOffset.UtcNow))
            return false;

        await _dispatch(decision.Command, cancellationToken);
        _acknowledge?.Invoke(decision.InboxItemId);
        if (_postResult is not null)
            await _postResult($"Applied: {decision.Command}", cancellationToken);
        return true;
    }
}
