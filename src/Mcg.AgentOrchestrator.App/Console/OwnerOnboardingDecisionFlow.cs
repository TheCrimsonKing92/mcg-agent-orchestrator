namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Onboarding writes project answers directly; goal-scoped decisions still submit operator intents.
internal sealed class OwnerOnboardingDecisionFlow(IOwnerQuestionSource questions, IOwnerConsoleDialogs dialogs)
{
    internal async Task RunAsync(OwnerConsoleDecision decision, bool accept, OwnerConsoleScreenOperation? operation,
        CancellationToken token, Action<string>? notice)
    {
        if (questions is not OnboardingQuestionSource source)
        { await Show("Onboarding answers are unavailable."); return; }
        if (!OnboardingQuestionSource.TryIdentify(decision.Id, out _, out _, out var stale))
        { await Show("Unknown onboarding item."); return; }
        if (!stale && !decision.CanAnswer)
        { await Show(decision.FullText); return; }
        var dismiss = stale && accept;
        string? text = null;
        if (dismiss)
        {
            if (!await dialogs.ConfirmAsync("Dismiss stale answer?", decision.FullText)) return;
        }
        else if (accept)
        {
            if (string.IsNullOrWhiteSpace(decision.ProposedDefault))
            { await Show($"question {decision.Number} has no proposed default"); return; }
            if (!await dialogs.ConfirmAsync("Accept default?", decision.ProposedDefault)) return;
            text = decision.ProposedDefault;
        }
        else
        {
            if (!decision.CanAnswer)
            { await Show("This key can no longer be answered; accept default to dismiss the stale answer."); return; }
            text = await dialogs.PromptTextAsync("Answer", decision.FullText);
            if (text is null) return;
        }

        var message = "";
        async Task Submit(CancellationToken stepToken)
        {
            var live = (await source.ReadAsync(stepToken)).Live.FirstOrDefault(item => item.ItemId == decision.Id);
            if (live is null) { message = $"question {decision.Number} is no longer open"; return; }
            if (live != decision.ToQuestion())
            { message = "Question changed; review it again before answering."; return; }
            var outcome = dismiss ? await source.DismissAsync(decision.Id, stepToken) :
                await source.AnswerAsync(decision.Id, text!, stepToken);
            message = outcome.Message;
            // The item remains open after refusal, with the attempted value visible inline.
            if (!outcome.Success && text is not null) message += $"\nAttempted answer: {text}";
        }
        if (operation is not null)
        {
            if (!await operation.RunAsync(dismiss ? "dismiss stale answer" : "answer", Submit, token)) return;
        }
        else await Submit(token);
        token.ThrowIfCancellationRequested();
        await Show(message);

        Task Show(string message)
        { notice?.Invoke(message); return dialogs.ShowTextAsync("Onboarding", message); }
    }
}
