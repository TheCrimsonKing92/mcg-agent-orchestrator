namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerDecisionWorkflow(IOwnerQuestionSource questions, IOwnerAnswerSubmitter answers,
    IOwnerConsoleDialogs dialogs, AnswerIntentStatusTracker tracking, OwnerQuestionResolutionReader? resolutions,
    TimeProvider clock)
{
    internal static bool AnswersInConsole(OwnerConsoleDecision decision) =>
        decision.Kind == OwnerQuestionKind.ExperimentReading || !OwnerQuestionViewOnly.IsViewOnly(decision.Kind);

    internal async Task ShowDecisionAsync(OwnerConsoleDecision decision, Func<OwnerConsoleViewModel?> model,
        Action<Action> subscribe, Action<Action> unsubscribe, OwnerConsoleScreenOperation? operation,
        CancellationToken cancellationToken)
    {
        using var detail = new OwnerConsoleDecisionDetail(decision, clock.LocalTimeZone,
            (target, accept, notify) => AnswerAsync(target, accept, operation, cancellationToken, notify),
            async token => resolutions is null || string.IsNullOrEmpty(decision.GoalId) ? null :
                (await resolutions.ListForGoalAsync(decision.GoalId, token)).FirstOrDefault(item => item.Id == decision.Id),
            text => dialogs.ShowTextAsync("Help", text), cancellationToken);
        void Refresh() => detail.Observe(model()?.Decisions.FirstOrDefault(item => item.Id == decision.Id));
        subscribe(Refresh);
        try { await dialogs.ShowDecisionAsync(detail); }
        finally { unsubscribe(Refresh); }
    }

    internal async Task AnswerAsync(OwnerConsoleDecision? decision, bool accept,
        OwnerConsoleScreenOperation? operation, CancellationToken cancellationToken, Action<string>? notice = null)
    {
        if (decision is null) return;
        if (decision.Kind == OwnerQuestionKind.Onboarding)
        {
            await new OwnerOnboardingDecisionFlow(questions, dialogs).RunAsync(decision, accept,
                operation, cancellationToken, notice);
            return;
        }
        if (decision.Kind == OwnerQuestionKind.ExperimentReading)
        {
            await new OwnerExperimentDecisionFlow(questions, answers, dialogs, tracking)
                .RunAsync(decision, operation, notice, cancellationToken);
            return;
        }
        if (!AnswersInConsole(decision))
        {
            await ShowNoticeAsync("View only", OwnerQuestionViewOnly.Notice(decision.Kind, decision.GoalId), notice);
            return;
        }
        string? text;
        if (accept)
        {
            if (string.IsNullOrWhiteSpace(decision.ProposedDefault))
            { await ShowNoticeAsync("Decision", $"question {decision.Number} has no proposed default", notice); return; }
            if (!await dialogs.ConfirmAsync("Accept default?", decision.ProposedDefault)) return;
            text = decision.ProposedDefault;
        }
        else text = await dialogs.PromptTextAsync("Answer", decision.FullText);
        if (text is null) return;
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("--", StringComparison.Ordinal))
        { await ShowNoticeAsync("Answer", "Answer cannot be empty or start with --", notice); return; }
        await SubmitAsync(decision, text, accept, operation, cancellationToken, notice);
    }

    private async Task SubmitAsync(OwnerConsoleDecision decision, string text, bool accept,
        OwnerConsoleScreenOperation? operation, CancellationToken cancellationToken, Action<string>? notice)
    {
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
            tracking.Track(submission.IntentId, decision.Number, decision.GoalPrefix, Notify, cancellationToken,
                text => { operation.ReportFailure(text); notice?.Invoke(text); });
            return;
        }
        await ShowNoticeAsync(title, message, notice);

        void Notify(string text) { operation!.Notify(text); notice?.Invoke(text); }
    }

    private Task ShowNoticeAsync(string heading, string text, Action<string>? notice)
    { notice?.Invoke(text); return dialogs.ShowTextAsync(heading, text); }

    private static async Task<bool> RunDependencyAsync(string label, Func<CancellationToken, Task> action,
        OwnerConsoleScreenOperation? operation, CancellationToken token)
    {
        if (operation is not null) return await operation.RunAsync(label, action, token);
        await action(token);
        return true;
    }
}
