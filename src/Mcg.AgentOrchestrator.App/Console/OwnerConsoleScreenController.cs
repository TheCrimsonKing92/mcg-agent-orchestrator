using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleScreenController(IOwnerQuestionSource questions, IOwnerAnswerSubmitter answers,
    IOwnerConsoleDialogs dialogs, IOrchestratorStateQueries state, IGoalEventTail tail,
    IOwnerConsoleConductor conductor, IOwnerConsoleDigestReport digest,
    IOwnerDigestSummary summary, TimeProvider clock, IOwnerConsoleEpicSource? epics = null,
    AnswerIntentStatusTracker? answerTracking = null) : IDisposable
{
    private readonly AnswerIntentStatusTracker _answerTracking = answerTracking ?? new(answers.ReadStatusAsync, clock);
    public void Dispose() => _answerTracking.Dispose();

    internal event Action? ModelApplied;

    internal async Task<OwnerConsoleEpicViewModel> LoadEpicViewAsync(OwnerConsoleEpicWindow window,
        string? detailId, CancellationToken cancellationToken)
    {
        var since = OwnerConsoleEpicViewModel.Cutoff(window, clock.GetUtcNow());
        var rows = epics is null ? [] : await epics.LoadAsync(since, cancellationToken).ConfigureAwait(false);
        var row = rows.FirstOrDefault(item => item.Epic.Id == detailId);
        var detail = row is null ? null : await OwnerConsoleEpicViewModel.DetailAsync(row, since, state, cancellationToken).ConfigureAwait(false);
        return new(window, since, rows, detail);
    }

    internal OwnerConsoleViewModel? Model { get; private set; }
    internal string? SelectedDecisionId { get; private set; }
    internal bool BellEnabled { get; private set; } = true;
    internal bool QuitRequested { get; private set; }
    internal Task ShowActivityMeaningAsync(OwnerConsoleActivityItem item) =>
        dialogs.ShowTextAsync("What this means", OwnerActivityNarrator.Explain(item));
    internal int SelectedIndex => Model is null ? -1 :
        Array.FindIndex(Model.Decisions.ToArray(), item => item.Id == SelectedDecisionId);

    internal void Apply(OwnerConsoleViewModel model)
    {
        var oldIndex = SelectedIndex;
        Model = model;
        if (!model.Decisions.Any(item => item.Id == SelectedDecisionId))
            SelectedDecisionId = model.Decisions.Length == 0 ? null :
                model.Decisions[Math.Clamp(oldIndex, 0, model.Decisions.Length - 1)].Id;
        ModelApplied?.Invoke();
    }

    internal void SelectIndex(int index)
    {
        if (Model is { Decisions.Length: > 0 })
            SelectedDecisionId = Model.Decisions[Math.Clamp(index, 0, Model.Decisions.Length - 1)].Id;
    }

    internal async Task HandleKeyAsync(ConsoleKey key, char character = '\0', OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        if (key == ConsoleKey.UpArrow) SelectIndex(SelectedIndex - 1);
        else if (key == ConsoleKey.DownArrow) SelectIndex(SelectedIndex + 1);
        else if (key == ConsoleKey.Enter && Selected() is { } detail)
            await dialogs.ShowTextAsync("Decision", $"{detail.GoalId} | {detail.Kind}\n{detail.FullText}\nblast radius: {detail.BlastRadius}\nconfidence: {detail.Confidence}\ndefault: {detail.ProposedDefault}");
        else if (character is 'a' or 'r') await AnswerAsync(character == 'a', operation, cancellationToken);
        else if (character == ':')
        {
            var command = await dialogs.PromptTextAsync("Command", OwnerConsoleKeyHints.CommandPrompt);
            if (command is not null) await RunCommandAsync(command, operation, cancellationToken);
        }
        else if (character == 'q') QuitRequested = true;
    }

    private OwnerConsoleDecision? Selected() => Model?.Decisions.FirstOrDefault(item => item.Id == SelectedDecisionId);

    internal Task ShowHelpAsync() => dialogs.ShowTextAsync("Help", OwnerConsoleKeyHints.HelpText);

    internal async Task ShowGoalDetailAsync(string goalId, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        var output = new OwnerConsoleCapturedOutput();
        if (!await RunDependencyAsync("goal detail", stepToken =>
            OwnerConsoleGoalDetailFormatter.ComposeAsync(state, tail, goalId, output, stepToken), operation, cancellationToken)) return;
        cancellationToken.ThrowIfCancellationRequested();
        await dialogs.ShowTextAsync("Goal", output.Text);
    }

    private async Task AnswerAsync(bool accept, OwnerConsoleScreenOperation? operation, CancellationToken cancellationToken)
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
        var title = "Answer";
        var message = string.Empty;
        OwnerAnswerSubmission? submission = null;
        if (!await RunDependencyAsync(accept ? "accept default" : "answer", async stepToken =>
        {
            try
            {
                var live = (await questions.ReadAsync(stepToken)).Live.FirstOrDefault(item => item.ItemId == decision.Id);
                if (live is null)
                { title = "Decision"; message = $"question {decision.Number} is no longer open"; return; }
                if (live != decision.ToQuestion())
                { title = "Decision"; message = "Question changed; review it again before answering."; return; }
                stepToken.ThrowIfCancellationRequested();
                submission = answers.Submit(live, text);
                message = AnswerIntentStatusTracker.Queued(decision.Number, decision.GoalPrefix);
            }
            catch (OperationCanceledException) when (stepToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { message = $"error: {ex.Message}"; }
        }, operation, cancellationToken)) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (submission is not null && operation is not null)
        {
            operation.Notify(message);
            _answerTracking.Track(submission.IntentId, decision.Number, decision.GoalPrefix, operation.Notify, cancellationToken);
            return;
        }
        await dialogs.ShowTextAsync(title, message);
    }

    internal async Task RunCommandAsync(string raw, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var line = raw.Trim().TrimStart(':').Trim();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var output = new OwnerConsoleCapturedOutput();
        var control = new OwnerConsoleControlCommands(conductor, digest, output, clock);
        if (!await RunDependencyAsync(line, ExecuteAsync, operation, cancellationToken)) return;
        cancellationToken.ThrowIfCancellationRequested();
        await dialogs.ShowTextAsync("Command result", output.Text);

        async Task ExecuteAsync(CancellationToken stepToken)
        {
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
                    await OwnerConsoleGoalDetail.ComposeAsync(state, tail, parts[1], output, stepToken);
                    break;
                default: output.WriteLine("unknown command"); break;
            }
        }
    }

    private static async Task<bool> RunDependencyAsync(string label, Func<CancellationToken, Task> action,
        OwnerConsoleScreenOperation? operation, CancellationToken cancellationToken)
    {
        if (operation is not null) return await operation.RunAsync(label, action, cancellationToken);
        await action(cancellationToken);
        return true;
    }
}
