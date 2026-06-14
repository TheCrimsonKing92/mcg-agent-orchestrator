using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class DiscordOperatorChannel : IOperatorChannel
{
    private readonly IDiscordForumApi _api;
    private readonly ulong _forumChannelId;
    private readonly string _stateDirectory;
    private readonly string? _dashboardBaseUrl;

    private const string ThreadStateFile = "discord-threads.json";

    public DiscordOperatorChannel(
        IDiscordForumApi api,
        ulong forumChannelId,
        string stateDirectory,
        string? dashboardBaseUrl = null)
    {
        _api = api;
        _forumChannelId = forumChannelId;
        _stateDirectory = stateDirectory;
        _dashboardBaseUrl = dashboardBaseUrl;
    }

    public string ChannelType => "discord";

    public async Task SendEscalationAsync(OperatorEscalation escalation, CancellationToken cancellationToken = default)
    {
        var threadId = await ResolveOrCreateThreadAsync(escalation, cancellationToken);
        var content = BuildContent(escalation);
        var buttons = BuildButtons(escalation);
        await _api.SendMessageAsync(threadId, content, buttons, cancellationToken);
    }

    private async Task<ulong> ResolveOrCreateThreadAsync(OperatorEscalation escalation, CancellationToken cancellationToken)
    {
        var saved = LoadSavedThreadId(escalation.GoalPrefix);
        if (saved.HasValue)
            return saved.Value;

        var title = $"[{escalation.GoalPrefix}] Orchestrator Escalations";
        var initial = $"Escalation channel for goal `{escalation.GoalPrefix}` ({escalation.GoalId}).";
        var threadId = await _api.CreateThreadAsync(_forumChannelId, title, initial, cancellationToken);
        SaveThreadId(escalation.GoalPrefix, threadId);
        return threadId;
    }

    private static string BuildContent(OperatorEscalation escalation)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**[{escalation.Kind}]** {escalation.Title}");
        sb.AppendLine();
        sb.AppendLine(escalation.Summary);
        sb.AppendLine();
        sb.AppendLine($"**Evidence:** {escalation.KeyEvidence}");
        return sb.ToString().Trim();
    }

    private static IReadOnlyList<DiscordButtonDefinition> BuildButtons(OperatorEscalation escalation)
    {
        var buttons = new List<DiscordButtonDefinition>();
        foreach (var action in escalation.Actions.Take(3))
        {
            if (action.RequiresInput)
                continue;

            var customId = action.RequiresConfirm
                ? DiscordInteractionHandler.BuildConfirmCustomId(escalation.InboxItemId, action.Command)
                : DiscordInteractionHandler.BuildDirectCustomId(escalation.InboxItemId, action.Command);

            var style = action.RequiresConfirm ? DiscordButtonStyle.Danger : DiscordButtonStyle.Primary;
            buttons.Add(new DiscordButtonDefinition(action.Label, customId, style));
        }
        return buttons;
    }

    private ulong? LoadSavedThreadId(string goalPrefix)
    {
        var state = LoadThreadState();
        return state.TryGetValue(goalPrefix, out var idStr) && ulong.TryParse(idStr, out var id)
            ? id
            : null;
    }

    private void SaveThreadId(string goalPrefix, ulong threadId)
    {
        var state = LoadThreadState();
        state[goalPrefix] = threadId.ToString();
        Directory.CreateDirectory(_stateDirectory);
        File.WriteAllText(
            Path.Combine(_stateDirectory, ThreadStateFile),
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Dictionary<string, string> LoadThreadState()
    {
        var path = Path.Combine(_stateDirectory, ThreadStateFile);
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? [];
        }
        catch
        {
            return [];
        }
    }
}
