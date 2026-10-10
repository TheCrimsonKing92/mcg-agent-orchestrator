namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerExperimentDecisionFlow(IOwnerQuestionSource questions, IOwnerAnswerSubmitter answers,
    IOwnerConsoleDialogs dialogs, AnswerIntentStatusTracker tracking)
{
    internal async Task RunAsync(OwnerConsoleDecision decision, OwnerConsoleScreenOperation? operation,
        Action<string>? notice, CancellationToken cancellationToken)
    {
        var shortId = OwnerExperimentReadingQuestion.ShortId(decision.FullText);
        var live = false;
        if (!await RunDependencyAsync("experiment decision state", async token =>
            live = await IsLiveAsync(token))) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (!live || shortId is null)
        { await RefuseAsync($"question {decision.Number} is no longer open"); return; }

        var form = await dialogs.PromptExperimentDecisionAsync("Decide experiment", decision.FullText,
            OwnerExperimentDecisionForm.DefaultEvidence(shortId));
        cancellationToken.ThrowIfCancellationRequested();
        if (form is null) return;
        if (form.Refusal() is { } refusal) { await RefuseAsync(refusal); return; }

        OwnerAnswerSubmission? submission = null;
        string? error = null;
        if (!await RunDependencyAsync("experiment decision", async token =>
        {
            try
            {
                if (!await IsLiveAsync(token))
                { error = $"question {decision.Number} is no longer open"; return; }
                token.ThrowIfCancellationRequested();
                submission = answers.SubmitExperimentDecision(shortId, form);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { error = $"error: {ex.Message}"; }
        })) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (error is not null) { await RefuseAsync(error); return; }
        var prefix = $"experiment {shortId}";
        var queued = AnswerIntentStatusTracker.Queued(decision.Number, prefix);
        if (operation is null)
        { notice?.Invoke(queued); await dialogs.ShowTextAsync("Answer", queued); return; }
        Notify(queued);
        tracking.Track(submission!.IntentId, decision.Number, prefix, Notify, cancellationToken,
            text => { operation.ReportFailure(text); notice?.Invoke(text); });

        async Task<bool> IsLiveAsync(CancellationToken token) =>
            (await questions.ReadAsync(token)).Live.FirstOrDefault(item => item.ItemId == decision.Id) == decision.ToQuestion();

        async Task<bool> RunDependencyAsync(string label, Func<CancellationToken, Task> action)
        {
            if (operation is not null) return await operation.RunAsync(label, action, cancellationToken);
            await action(cancellationToken);
            return true;
        }

        void Notify(string text) { operation!.Notify(text); notice?.Invoke(text); }

        Task RefuseAsync(string text)
        {
            notice?.Invoke(text);
            if (operation is null) return dialogs.ShowTextAsync("Decision", text);
            operation.ReportFailure(text);
            return Task.CompletedTask;
        }
    }
}
