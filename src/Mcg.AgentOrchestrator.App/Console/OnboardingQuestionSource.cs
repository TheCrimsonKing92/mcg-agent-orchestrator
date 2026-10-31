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
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<OwnerConsoleBoardProject> scope;
        try { scope = projects(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Onboarding registry availability must not hide or block goal-scoped decisions.
            return snapshot;
        }
        var live = snapshot.Live.ToList();
        foreach (var project in scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.Name.Equals("home", StringComparison.OrdinalIgnoreCase) || project.Error is not null) continue;
            var goalLanded = await ProjectOnboardingCompletion.HasLandedGoalAsync(project.StatePath, cancellationToken);
            var state = Read(project, goalLanded);
            if (!state.IsOnboarding || state.Model is null) continue;
            var answers = ProjectOwnerAnswerRecorder.ReadAnswers(DirectoryFor(project)).Answers;
            foreach (var question in state.OpenQuestions)
            {
                var resolution = OnboardingQuestionResolution.Classify(question.FactKey, project.Name, project.UseLearnedBranch);
                var recorded = answers.FirstOrDefault(answer => answer.FactKey == question.FactKey);
                var text = $"{question.FactKey}: {question.Question}\nProject: {project.Name}\nFact key: {question.FactKey}";
                if (CurrentValue(state.Model, question.FactKey) is { } current) text += $"\nCurrent value: {current}";
                if (question.SuggestedValue is { } hint) text += $"\nSuggested value: {hint}";
                if (recorded is not null) text += $"\nRecorded answer: {recorded.Value}";
                if (question.FactKey is ProjectDelivery.ModeKey or ProjectDelivery.DeciderKey)
                    text += $"\nUse project answer {project.Name} {question.FactKey} <value> for this registry answer.";
                if (resolution.Command is { } command) text += $"\nResolve with: {command}";
                live.Add(new(Id(project.Name, question.FactKey, false), "", OwnerQuestionKind.Onboarding, text,
                    ProposedDefault: resolution.Command is null ? question.SuggestedValue : null,
                    CanAnswer: resolution.Kind != OnboardingQuestionResolutionKind.Command));
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
        OwnerConsoleBoardProject? project;
        try { project = projects().FirstOrDefault(project => project.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectOwnerAnswerOutcome(false, $"{name}: project unavailable. {ex.Message}"));
        }
        if (project is null || project.Error is not null)
            return Task.FromResult(new ProjectOwnerAnswerOutcome(false, $"{name}: project unavailable."));
        ProjectOwnerAnswerOutcome result;
        if (dismiss && !stale) result = new(false, "Only a stale answer can be dismissed.");
        else if (!dismiss && key is ProjectDelivery.ModeKey or ProjectDelivery.DeciderKey)
            result = new(false, $"Use project answer {name} {key} <value> for this registry answer.");
        else if (!dismiss && key == LearnedIntegrationBranch.OwnerQuestionKey)
            result = new(false, $"Resolve with: project use-learned-branch {name}");
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
    private static ProjectOnboardingState Read(OwnerConsoleBoardProject project, bool goalLanded) =>
        ProjectOnboardingStateReader.Read(project.Name, DirectoryFor(project), project.ConductEventsLogPath,
            project.DeliveryAnswered, project.UseLearnedBranch, goalLanded);

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
