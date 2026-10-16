using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleScreenController(IOwnerQuestionSource questions, IOwnerAnswerSubmitter answers,
    IOwnerConsoleDialogs dialogs, IOrchestratorStateQueries state, IGoalEventTail tail,
    IOwnerConsoleConductor conductor, IOwnerConsoleDigestReport digest,
    IOwnerDigestSummary summary, TimeProvider clock, IOwnerConsoleEpicSource? epics = null,
    AnswerIntentStatusTracker? answerTracking = null, OwnerQuestionResolutionReader? resolutions = null,
    IOwnerConsoleNotifier? notifier = null, OwnerConsoleNotificationSettings? notifications = null,
    Func<OwnerFleetViewModel>? fleet = null) : IDisposable
{
    private readonly object _notificationGate = new();
    private readonly IOwnerConsoleNotifier _notifier = notifier ?? new NullOwnerConsoleNotifier();
    private readonly OwnerConsoleNotificationDetector _notifications = new(notifications ?? new());
    private OwnerConsoleBellTiers _bellTiers = OwnerConsoleBellTiers.All;
    private readonly AnswerIntentStatusTracker _answerTracking = answerTracking ?? new(answers.ReadStatusAsync, clock);
    public void Dispose() => _answerTracking.Dispose();

    internal event Action? ModelApplied;

    internal async Task<OwnerConsoleEpicViewModel?> LoadEpicViewAsync(OwnerConsoleEpicWindow window,
        string? detailId, CancellationToken cancellationToken)
    {
        if (epics is null) return null;
        var since = OwnerConsoleEpicViewModel.Cutoff(window, clock.GetUtcNow());
        if (detailId is not null)
        {
            var snapshot = await epics.LoadDetailAsync(detailId, since, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(window, since, [snapshot.Row],
                OwnerConsoleEpicViewModel.BuildDetail(snapshot.Row, snapshot.Since, snapshot.Goals), clock.GetLocalNow());
        }
        var rows = await epics.LoadAsync(since, cancellationToken).ConfigureAwait(false);
        return new(window, since, rows, LoadedAt: clock.GetLocalNow());
    }

    internal OwnerConsoleViewModel? Model { get; private set; }
    internal string? SelectedDecisionId { get; private set; }
    internal bool BellEnabled { get { lock (_notificationGate) return _bellTiers != OwnerConsoleBellTiers.None; } }
    internal string BellStatusText { get { lock (_notificationGate) return _bellTiers.StatusText; } }
    internal bool QuitRequested { get; private set; }
    private string? _requestedEpicId;
    internal string? TakeRequestedEpicId()
    { var id = _requestedEpicId; _requestedEpicId = null; return id; }
    internal async Task ShowActivityMeaningAsync(OwnerConsoleActivityItem item, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        if (item.Kind == "owner-question-resolved" && resolutions is not null)
        {
            OwnerQuestionResolution? resolution = null;
            if (!await RunDependencyAsync("question resolution", async stepToken =>
                resolution = await resolutions.ReadAsync(item, stepToken), operation, cancellationToken)) return;
            if (resolution is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await dialogs.ShowTextAsync("What this means", OwnerQuestionResolutionText.Build(resolution, clock.LocalTimeZone));
                return;
            }
        }
        IReadOnlyList<OwnerConsoleDecision> live = [];
        if (!await RunDependencyAsync("activity decision state", async stepToken =>
        {
            var snapshot = await questions.ReadAsync(stepToken);
            live = Model?.Decisions.Where(decision => snapshot.Live.Contains(decision.ToQuestion())).ToArray() ?? [];
        }, operation, cancellationToken)) return;
        cancellationToken.ThrowIfCancellationRequested();
        await dialogs.ShowTextAsync("What this means", OwnerActivityNarrator.Explain(item, live));
    }
    internal int SelectedIndex => Model is null ? -1 :
        Array.FindIndex(Model.Decisions.ToArray(), item => item.Id == SelectedDecisionId);

    internal void Apply(OwnerConsoleViewModel model)
    {
        IReadOnlyList<OwnerConsoleNotificationTier> tiers;
        lock (_notificationGate)
        {
            tiers = _notifications.Detect(Model, model, clock.GetUtcNow(), _bellTiers);
            var oldIndex = SelectedIndex;
            Model = model;
            if (!model.Decisions.Any(item => item.Id == SelectedDecisionId))
                SelectedDecisionId = model.Decisions.Length == 0 ? null :
                    model.Decisions[Math.Clamp(oldIndex, 0, model.Decisions.Length - 1)].Id;
        }
        foreach (var tier in tiers)
        {
            try { _notifier.Notify(tier); }
            catch { /* Sound failure must not break rendering. */ }
        }
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
            await ShowDecisionAsync(detail, operation, cancellationToken);
        else if (character is 'a' or 'r') await AnswerAsync(character == 'a', operation, cancellationToken);
        else if (character == ':')
        {
            var command = await dialogs.PromptTextAsync("Command", OwnerConsoleKeyHints.CommandPrompt);
            if (command is not null) await RunCommandAsync(command, operation, cancellationToken);
        }
        else if (character == 'q') QuitRequested = true;
    }

    private OwnerConsoleDecision? Selected() => Model?.Decisions.FirstOrDefault(item => item.Id == SelectedDecisionId);

    internal OwnerConsoleDecision? SelectedDecision => Selected();

    internal static bool AnswersInConsole(OwnerConsoleDecision decision) =>
        decision.Kind == OwnerQuestionKind.ExperimentReading || !OwnerQuestionViewOnly.IsViewOnly(decision.Kind);

    internal Task ShowHelpAsync() => dialogs.ShowTextAsync("Help", OwnerConsoleKeyHints.HelpText);

    internal Task ShowDecisionAsync(OwnerConsoleDecision decision) => ShowDecisionAsync(decision, null, CancellationToken.None);

    internal string ResolutionText(OwnerQuestionResolution resolution) =>
        OwnerQuestionResolutionText.Build(resolution, clock.LocalTimeZone);

    private async Task ShowDecisionAsync(OwnerConsoleDecision decision, OwnerConsoleScreenOperation? operation,
        CancellationToken cancellationToken)
    {
        using var detail = new OwnerConsoleDecisionDetail(decision, clock.LocalTimeZone,
            (target, accept, notify) => AnswerAsync(accept, operation, cancellationToken, target, notify),
            async token => resolutions is null || string.IsNullOrEmpty(decision.GoalId) ? null :
                (await resolutions.ListForGoalAsync(decision.GoalId, token)).FirstOrDefault(item => item.Id == decision.Id),
            text => dialogs.ShowTextAsync("Help", text), cancellationToken);
        void Refresh() => detail.Observe(Model?.Decisions.FirstOrDefault(item => item.Id == decision.Id));
        ModelApplied += Refresh;
        try { await dialogs.ShowDecisionAsync(detail); }
        finally { ModelApplied -= Refresh; }
    }

    internal async Task ShowGoalDetailAsync(string goalId, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        OwnerConsoleGoalDialog.Content? content = null;
        if (!await RunDependencyAsync("goal detail", async stepToken =>
            content = await ComposeGoalAsync(goalId, stepToken), operation, cancellationToken)) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (content is null) { await dialogs.ShowTextAsync("Goal", "goal state unavailable"); return; }
        using var dialog = new OwnerConsoleGoalDialog(this, content, token => ComposeGoalAsync(goalId, token),
            decision => ShowDecisionAsync(decision, operation, cancellationToken), dialogs.ShowTextAsync, cancellationToken);
        await dialogs.ShowGoalAsync(dialog);
        _requestedEpicId = dialog.RequestedEpicId;
    }

    private async Task<OwnerConsoleGoalDialog.Content?> ComposeGoalAsync(string goalId, CancellationToken token)
    {
        var output = new OwnerConsoleCapturedOutput();
        var goal = await OwnerConsoleGoalDetailFormatter.ComposeAsync(state, tail, goalId, output, token, clock.LocalTimeZone).ConfigureAwait(false);
        if (goal is null) return null;
        var resolved = resolutions is null ? [] : await resolutions.ListForGoalAsync(goalId, token).ConfigureAwait(false);
        var lines = output.Text.Replace("\r", "").TrimEnd('\n').Split('\n').ToList();
        var choiceLines = new List<int>();
        if (resolved.Count > 0)
        {
            lines.Add("Resolved questions (Enter to open):");
            foreach (var question in resolved)
            { choiceLines.Add(lines.Count); lines.Add(OwnerQuestionResolutionText.Summary(question, clock.LocalTimeZone)); }
        }
        var openQuestion = Model?.Decisions.Where(item => item.GoalId == goalId).OrderBy(item => item.Number).FirstOrDefault();
        var epicId = epics is null ? null : await epics.FindGoalEpicAsync(goalId, token).ConfigureAwait(false);
        var failure = goal.RetainedAcceptanceFailure is { } recorded ? OwnerConsoleGoalLanding.FailureText(recorded, clock.LocalTimeZone) : null;
        return new(string.Join(Environment.NewLine, lines), choiceLines, resolved, openQuestion, failure, epicId);
    }

    private async Task AnswerAsync(bool accept, OwnerConsoleScreenOperation? operation, CancellationToken cancellationToken,
        OwnerConsoleDecision? target = null, Action<string>? notice = null)
    {
        var decision = target ?? Selected();
        if (decision is null) return;
        if (decision.Kind == OwnerQuestionKind.ExperimentReading)
        {
            await new OwnerExperimentDecisionFlow(questions, answers, dialogs, _answerTracking)
                .RunAsync(decision, operation, notice, cancellationToken);
            return;
        }
        if (!AnswersInConsole(decision))
        {
            await ShowNoticeAsync("View only", OwnerQuestionViewOnly.Notice(decision.Kind, decision.GoalId));
            return;
        }
        string? text;
        if (accept)
        {
            if (string.IsNullOrWhiteSpace(decision.ProposedDefault))
            { await ShowNoticeAsync("Decision", $"question {decision.Number} has no proposed default"); return; }
            if (!await dialogs.ConfirmAsync("Accept default?", decision.ProposedDefault)) return;
            text = decision.ProposedDefault;
        }
        else text = await dialogs.PromptTextAsync("Answer", decision.FullText);
        if (text is null) return;
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("--", StringComparison.Ordinal))
        { await ShowNoticeAsync("Answer", "Answer cannot be empty or start with --"); return; }
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
            Notify(message);
            _answerTracking.Track(submission.IntentId, decision.Number, decision.GoalPrefix, Notify, cancellationToken,
                text => { operation.ReportFailure(text); notice?.Invoke(text); });
            return;
        }
        await ShowNoticeAsync(title, message);

        void Notify(string text) { operation!.Notify(text); notice?.Invoke(text); }
        Task ShowNoticeAsync(string heading, string text)
        { notice?.Invoke(text); return dialogs.ShowTextAsync(heading, text); }
    }

    internal async Task RunCommandAsync(string raw, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var line = raw.Trim().TrimStart(':').Trim();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var output = new OwnerConsoleCapturedOutput();
        string? goalId = null;
        string? fleetText = null;
        var control = new OwnerConsoleControlCommands(conductor, digest, output, clock);
        if (!await RunDependencyAsync(line, ExecuteAsync, operation, cancellationToken)) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (goalId is not null) { await ShowGoalDetailAsync(goalId, operation, cancellationToken); return; }
        if (fleetText is not null) { await dialogs.ShowTextAsync("Fleet", fleetText); return; }
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
                case "fleet":
                    if (parts.Length != 1) output.WriteLine("usage: fleet");
                    else if (fleet is null) output.WriteLine("fleet view unavailable in this session");
                    else fleetText = OwnerFleetDiagram.Render(fleet());
                    break;
                case "bell":
                    var arguments = parts.Skip(1).Select(part => part.ToLowerInvariant()).ToArray();
                    if (arguments.Length == 1 && arguments[0] is "on" or "off")
                    {
                        lock (_notificationGate) _bellTiers = arguments[0] == "on" ? OwnerConsoleBellTiers.All : OwnerConsoleBellTiers.None;
                        output.WriteLine($"bell {arguments[0]}");
                    }
                    else if (arguments.Length == 2 && arguments[0] is "calm" or "attention" or "alarm" &&
                        arguments[1] is "on" or "off")
                    {
                        var tier = arguments[0] switch
                        {
                            "calm" => OwnerConsoleNotificationTier.Calm,
                            "attention" => OwnerConsoleNotificationTier.Attention,
                            _ => OwnerConsoleNotificationTier.Alarm
                        };
                        lock (_notificationGate) _bellTiers = _bellTiers.With(tier, arguments[1] == "on");
                        output.WriteLine($"bell {arguments[0]} {arguments[1]}");
                    }
                    else output.WriteLine("usage: bell on|off | bell <calm|attention|alarm> on|off");
                    break;
                case "goal":
                    if (parts.Length != 2) { output.WriteLine("usage: goal <id-prefix>"); break; }
                    goalId = await OwnerConsoleGoalDetail.ResolveAsync(state, parts[1], output, stepToken);
                    break;
                default:
                    output.WriteLine("unknown command");
                    output.WriteLine("valid commands: " + OwnerConsoleKeyHints.CommandPrompt);
                    break;
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
