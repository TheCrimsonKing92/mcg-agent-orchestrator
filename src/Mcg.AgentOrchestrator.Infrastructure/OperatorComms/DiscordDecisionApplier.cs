namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class DiscordDecisionApplier
{
    private readonly string _auditDirectory;
    private readonly Func<string, CancellationToken, Task> _dispatch;
    private readonly Action<string>? _acknowledge;
    private readonly Func<string, CancellationToken, Task>? _postResult;
    private readonly ICollaborationItemStore _collaborationStore;
    private readonly IReadOnlyList<string> _allowedUserIds;
    private readonly Func<string, CancellationToken, Task<long?>>? _currentGoalStateVersion;
    private readonly Func<DateTimeOffset> _clock;

    public DiscordDecisionApplier(
        string auditDirectory,
        Func<string, CancellationToken, Task> dispatch,
        ICollaborationItemStore collaborationStore,
        IReadOnlyList<string> allowedUserIds,
        Action<string>? acknowledge = null,
        Func<string, CancellationToken, Task>? postResult = null,
        Func<string, CancellationToken, Task<long?>>? currentGoalStateVersion = null,
        Func<DateTimeOffset>? clock = null)
    {
        _auditDirectory = auditDirectory;
        _dispatch = dispatch;
        _acknowledge = acknowledge;
        _postResult = postResult;
        _collaborationStore = collaborationStore;
        _allowedUserIds = allowedUserIds;
        _currentGoalStateVersion = currentGoalStateVersion;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<bool> ApplyAsync(OperatorDecision decision, CancellationToken cancellationToken = default)
    {
        var userId = decision.ActorId.StartsWith("discord:", StringComparison.Ordinal)
            ? decision.ActorId["discord:".Length..]
            : decision.ActorId;
        if (!_allowedUserIds.Contains(userId, StringComparer.Ordinal))
        {
            await _collaborationStore.RecordRejectedDecisionAsync(
                decision.InboxItemId,
                decision.ActionIndex,
                decision.ActorId,
                decision.IdempotencyKey,
                $"User '{userId}' is not in the operator allowlist.",
                _clock(),
                cancellationToken);
            return false;
        }

        var currentVersion = _currentGoalStateVersion is null
            ? null
            : await _currentGoalStateVersion(decision.InboxItemId, cancellationToken);
        var claim = await _collaborationStore.TryClaimActionAsync(
            decision.InboxItemId,
            decision.ActionIndex,
            decision.ActorId,
            decision.IdempotencyKey,
            currentVersion,
            _clock(),
            cancellationToken);
        if (!claim.Applied)
            return false;

        var command = PrepareCommand(claim.Action!.Command, decision.FreeText);
        await _dispatch(command, cancellationToken);
        _acknowledge?.Invoke(decision.InboxItemId);
        if (_postResult is not null)
            await _postResult($"Applied: {command}", cancellationToken);
        await _collaborationStore.TryResolveAsync(decision.InboxItemId, command, cancellationToken);
        return true;
    }

    private string PrepareCommand(string command, string? freeText)
    {
        if (string.IsNullOrWhiteSpace(freeText))
            return command;

        Directory.CreateDirectory(_auditDirectory);
        var inputDirectory = Path.Combine(_auditDirectory, "discord-modal-text");
        Directory.CreateDirectory(inputDirectory);
        var path = Path.Combine(inputDirectory, $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, freeText);
        var quotedPath = "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        foreach (var placeholder in new[] { "<answer>", "<note>", "<command>" })
        {
            if (command.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                return command.Replace(placeholder, $"--text-file {quotedPath}", StringComparison.OrdinalIgnoreCase);
            }
        }

        return $"{command} --text-file {quotedPath}";
    }
}
