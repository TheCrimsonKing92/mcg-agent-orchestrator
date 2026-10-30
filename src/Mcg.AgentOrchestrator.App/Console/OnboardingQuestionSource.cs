using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Project identity plus the fact key owns routing; these items never acquire a goal id.
internal sealed class OnboardingQuestionSource(IOwnerQuestionSource inner,
    Func<IReadOnlyList<OwnerConsoleBoardProject>> projects, TimeProvider clock, Action? changed = null) : IOwnerQuestionSource
{
    public async Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken)).Live;

    public async Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await inner.ReadAsync(cancellationToken);
        var live = snapshot.Live.ToList();
        foreach (var project in projects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.Name.Equals("home", StringComparison.OrdinalIgnoreCase) || project.Error is not null) continue;
            var state = Read(project);
            if (state.Model is null) continue;
            var answers = ProjectOwnerAnswerRecorder.ReadAnswers(DirectoryFor(project)).Answers;
            foreach (var question in state.OpenQuestions)
            {
                var recorded = answers.FirstOrDefault(answer => answer.FactKey == question.FactKey);
                var text = $"{question.FactKey}: {question.Question}\nProject: {project.Name}\nFact key: {question.FactKey}";
                if (CurrentValue(state.Model, question.FactKey) is { } current) text += $"\nCurrent value: {current}";
                if (question.SuggestedValue is { } hint) text += $"\nSuggested value: {hint}";
                if (recorded is not null) text += $"\nRecorded answer: {recorded.Value}";
                if (question.FactKey is ProjectDelivery.ModeKey or ProjectDelivery.DeciderKey)
                    text += $"\nUse project answer {project.Name} {question.FactKey} <value> for this registry answer.";
                live.Add(new(Id(project.Name, question.FactKey, false), "", OwnerQuestionKind.Onboarding, text,
                    ProposedDefault: question.SuggestedValue));
            }
            foreach (var stale in state.StaleAnswers)
            {
                var key = stale.Answer.FactKey;
                var canAnswer = OwnerAnswerApplier.IsLiveKey(state.Model, key);
                var text = $"{key}: stale answer\nProject: {project.Name}\nFact key: {key}\nStale answer: {stale.Answer.Value}\n{stale.Reason}";
                if (CurrentValue(state.Model, key) is { } current) text += $"\nCurrent value: {current}";
                var question = state.Model.OwnerQuestions.FirstOrDefault(question => question.FactKey == key);
                if (question is not null) text += $"\n{question.Question}";
                text += canAnswer ? "\nAnswer to replace it, or accept default to dismiss." : "\nAccept default to dismiss; this key can no longer be answered.";
                live.Add(new(Id(project.Name, key, true), "", OwnerQuestionKind.Onboarding, text,
                    ProposedDefault: "Dismiss stale answer", CanAnswer: canAnswer));
            }
        }
        return snapshot with { Live = live };
    }

    internal Task<ProjectOwnerAnswerOutcome> AnswerAsync(string itemId, string text, CancellationToken token = default) =>
        MutateAsync(itemId, text, dismiss: false, token);

    internal Task<ProjectOwnerAnswerOutcome> DismissAsync(string itemId, CancellationToken token = default) =>
        MutateAsync(itemId, null, dismiss: true, token);

    private Task<ProjectOwnerAnswerOutcome> MutateAsync(string itemId, string? text, bool dismiss, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TryIdentify(itemId, out var name, out var key, out var stale))
            return Task.FromResult(new ProjectOwnerAnswerOutcome(false, "Unknown onboarding item."));
        var project = projects().FirstOrDefault(project => project.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (project is null || project.Error is not null)
            return Task.FromResult(new ProjectOwnerAnswerOutcome(false, $"{name}: project unavailable."));
        ProjectOwnerAnswerOutcome result;
        if (dismiss && !stale) result = new(false, "Only a stale answer can be dismissed.");
        else if (!dismiss && key is ProjectDelivery.ModeKey or ProjectDelivery.DeciderKey)
            result = new(false, $"Use project answer {name} {key} <value> for this registry answer.");
        else if (dismiss) result = ProjectOwnerAnswerRecorder.Remove(DirectoryFor(project), key);
        else if (stale) result = ProjectOwnerAnswerRecorder.ReplaceStale(DirectoryFor(project), key, text!, null, clock.GetUtcNow());
        else result = ProjectOwnerAnswerRecorder.Record(DirectoryFor(project), key, text!, null, clock.GetUtcNow());
        if (result.Success) changed?.Invoke();
        return Task.FromResult(result);
    }

    internal static bool TryIdentify(string id, out string project, out string key, out bool stale)
    {
        var parts = id.Split(':', 3);
        stale = parts[0] == "onboarding-stale";
        project = parts.Length == 3 ? Uri.UnescapeDataString(parts[1]) : "";
        key = parts.Length == 3 ? Uri.UnescapeDataString(parts[2]) : "";
        return parts.Length == 3 && (stale || parts[0] == "onboarding") && project.Length > 0 && key.Length > 0;
    }

    private static string Id(string project, string key, bool stale) =>
        $"{(stale ? "onboarding-stale" : "onboarding")}:{Uri.EscapeDataString(project)}:{Uri.EscapeDataString(key)}";
    private static string DirectoryFor(OwnerConsoleBoardProject project) =>
        project.OrchestratorDirectory ?? Path.GetDirectoryName(project.PortfolioPath)!;
    private static ProjectOnboardingState Read(OwnerConsoleBoardProject project) =>
        ProjectOnboardingStateReader.Read(project.Name, DirectoryFor(project), project.ConductEventsLogPath, project.DeliveryAnswered);

    private static string? CurrentValue(ProjectModel model, string key)
    {
        if (key == LearnedIntegrationBranch.OwnerQuestionKey) return model.IntegrationBranch?.Value;
        if (key.StartsWith("environment/", StringComparison.Ordinal))
            return model.EnvironmentNeeds.FirstOrDefault(need => need.Name == key["environment/".Length..])?.Value.Value;
        var parts = key.Split('/');
        if (parts.Length == 3 && parts[0] == "commands")
        {
            var commands = model.Commands.FirstOrDefault(commands => commands.UnitId == parts[1]);
            if (parts[2] == "build") return commands?.BuildCommand.Value;
            if (parts[2] == "test") return commands?.TestCommand?.Value;
        }
        return model.EnvironmentNeeds.FirstOrDefault(need => need.Name == key)?.Value.Value;
    }
}
