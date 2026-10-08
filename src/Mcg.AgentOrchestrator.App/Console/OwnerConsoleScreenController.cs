using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleScreenController(IOwnerQuestionSource questions, IOwnerAnswerSubmitter answers,
    IOwnerConsoleDialogs dialogs, IOrchestratorStateQueries state, IGoalEventTail tail,
    IOwnerConsoleConductor conductor, IOwnerConsoleDigestReport digest,
    IOwnerDigestSummary summary, TimeProvider clock)
{
    internal OwnerConsoleViewModel? Model { get; private set; }
    internal string? SelectedDecisionId { get; private set; }
    internal bool BellEnabled { get; private set; } = true;
    internal bool QuitRequested { get; private set; }
    internal int SelectedIndex => Model is null ? -1 :
        Array.FindIndex(Model.Decisions.ToArray(), item => item.Id == SelectedDecisionId);

    internal void Apply(OwnerConsoleViewModel model)
    {
        var oldIndex = SelectedIndex;
        Model = model;
        if (!model.Decisions.Any(item => item.Id == SelectedDecisionId))
            SelectedDecisionId = model.Decisions.Length == 0 ? null :
                model.Decisions[Math.Clamp(oldIndex, 0, model.Decisions.Length - 1)].Id;
    }

    internal void SelectIndex(int index)
    {
        if (Model is { Decisions.Length: > 0 })
            SelectedDecisionId = Model.Decisions[Math.Clamp(index, 0, Model.Decisions.Length - 1)].Id;
    }

    internal async Task HandleKeyAsync(ConsoleKey key, char character = '\0', CancellationToken cancellationToken = default)
    {
        if (key == ConsoleKey.UpArrow) SelectIndex(SelectedIndex - 1);
        else if (key == ConsoleKey.DownArrow) SelectIndex(SelectedIndex + 1);
        else if (key == ConsoleKey.Enter && Selected() is { } detail)
            await dialogs.ShowTextAsync("Decision", $"{detail.GoalId} | {detail.Kind}\n{detail.FullText}\nblast radius: {detail.BlastRadius}\nconfidence: {detail.Confidence}\ndefault: {detail.ProposedDefault}");
        else if (character is 'a' or 'r') await AnswerAsync(character == 'a', cancellationToken);
        else if (character == ':')
        {
            var command = await dialogs.PromptTextAsync("Command", "conductor start|stop|status | digest | metrics | bell on|off | goal <id>");
            if (command is not null) await RunCommandAsync(command, cancellationToken);
        }
        else if (character == 'q') QuitRequested = true;
    }

    private OwnerConsoleDecision? Selected() => Model?.Decisions.FirstOrDefault(item => item.Id == SelectedDecisionId);

    private async Task AnswerAsync(bool accept, CancellationToken cancellationToken)
    {
        var decision = Selected();
        if (decision is null) return;
        if (decision.Kind == OwnerQuestionKind.StewardHold)
        {
            await dialogs.ShowTextAsync("View only", $"Steward questions are answered through goal verbs for now; use the CLI retry/adjudicate commands for goal {decision.GoalId}.");
            return;
        }
        string? text;
        if (accept)
        {
            if (string.IsNullOrWhiteSpace(decision.ProposedDefault))
            { await dialogs.ShowTextAsync("Decision", $"question {decision.Number} has no proposed default"); return; }
            if (!await dialogs.ConfirmAsync("Accept default?", decision.ProposedDefault)) return;
            text = decision.ProposedDefault;
        }
        else text = await dialogs.PromptTextAsync("Answer", decision.FullText);
        if (text is null) return;
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("--", StringComparison.Ordinal))
        { await dialogs.ShowTextAsync("Answer", "Answer cannot be empty or start with --"); return; }
        try
        {
            var live = (await questions.ReadAsync(cancellationToken)).Live.FirstOrDefault(item => item.ItemId == decision.Id);
            if (live is null)
            { await dialogs.ShowTextAsync("Decision", $"question {decision.Number} is no longer open"); return; }
            if (live != decision.ToQuestion())
            { await dialogs.ShowTextAsync("Decision", "Question changed; review it again before answering."); return; }
            cancellationToken.ThrowIfCancellationRequested();
            answers.Submit(live, text);
            var stillOpen = (await questions.ReadAsync(cancellationToken)).Live.Any(item => item.ItemId == decision.Id);
            await dialogs.ShowTextAsync("Answer", stillOpen ? $"question {decision.Number} is still open; answer was not accepted" : $"answered question {decision.Number}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        { await dialogs.ShowTextAsync("Answer", $"error: {ex.Message}"); }
    }

    internal async Task RunCommandAsync(string raw, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var line = raw.Trim().TrimStart(':').Trim();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var output = new OwnerConsoleCapturedOutput();
        var control = new OwnerConsoleControlCommands(conductor, digest, output, clock);
        switch (parts[0].ToLowerInvariant())
        {
            case "conductor": control.HandleConductor(line); break;
            case "digest":
                if (parts.Length != 1) output.WriteLine("usage: digest");
                else foreach (var item in summary.ReadSummaryLines().Take(5)) output.WriteLine(item);
                break;
            case "metrics": control.HandleMetrics(line); break;
            case "bell":
                if (parts.Length != 2 || parts[1] is not ("on" or "off")) output.WriteLine("usage: bell on|off");
                else { BellEnabled = parts[1] == "on"; output.WriteLine($"bell {parts[1]}"); }
                break;
            case "goal":
                if (parts.Length != 2) { output.WriteLine("usage: goal <id-prefix>"); break; }
                var matches = (await state.ListGoalMetadataAsync(cancellationToken))
                    .Where(item => item.Id.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                { output.WriteLine(matches.Length == 0 ? $"no goal matches '{parts[1]}'" : "ambiguous goal"); break; }
                var kernel = await state.LoadGoalsAsync([new GoalId(matches[0].Id)], cancellationToken);
                var goal = kernel.Goals.SingleOrDefault(item => item.Id.Value == matches[0].Id);
                if (goal is null) { output.WriteLine("goal state unavailable"); break; }
                output.WriteLine($"{goal.Id.Value} | {OwnerGoalTitle.From(goal.Objective)} | {goal.Status}");
                foreach (var item in tail.ReadLast(goal.Id.Value, 15)) output.WriteLine(item);
                break;
            default: output.WriteLine("unknown command"); break;
        }
        await dialogs.ShowTextAsync("Command result", output.Text);
    }
}
