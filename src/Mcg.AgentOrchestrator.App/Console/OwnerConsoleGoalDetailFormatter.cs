using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGoalDetailFormatter
{
    internal static Task<Goal?> ComposeAsync(IOrchestratorStateQueries state, IGoalEventTail tail,
        string id, IOwnerConsoleOutput output, CancellationToken token, TimeZoneInfo? zone = null,
        Func<OwnerConsoleGateProgress?>? gate = null)
        => ComposeAsync(new OwnerGoalDetailQuery(state, tail, gate), id, output, token, zone);

    internal static async Task<Goal?> ComposeAsync(OwnerGoalDetailQuery query, string id,
        IOwnerConsoleOutput output, CancellationToken token, TimeZoneInfo? zone = null,
        Func<OwnerConsoleGateProgress?>? gate = null, string? priorityWarning = null)
    {
        var detail = await query.LoadAsync(id, token);
        if (detail is null) { output.WriteLine("goal state unavailable"); return null; }
        if (gate is not null) detail = query.Observe(detail, gate());
        detail = detail with { PriorityWarning = priorityWarning };
        Render(detail, output, zone ?? TimeZoneInfo.Local);
        return detail.Goal;
    }

    internal static void Render(OwnerGoalDetailSnapshot detail, IOwnerConsoleOutput output, TimeZoneInfo zone)
    {
        var goal = detail.Goal;
        output.WriteLine(goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)] + "  " + OwnerGoalTitle.From(goal.Objective));
        var status = OwnerConsoleStatusLabel.For(goal.Status);
        output.WriteLine("Status: " + status);
        if (OwnerConsolePriorityText.DetailLine(goal) is { } priority) output.WriteLine(priority);
        if (detail.PriorityWarning is { } warning) output.WriteLine("Priority warning: " + warning);
        if (goal.Isolation == GoalIsolation.Solo) output.WriteLine("Isolation: solo");
        if (goal.SourceBacklogItemId is { } source)
            output.WriteLine("Source: backlog " + source[..Math.Min(8, source.Length)] +
                (goal.SourceBacklogCoverage is { } coverage ? ", " + coverage.ToString().ToLowerInvariant() : ""));
        var stage = GoalLifecycle.ResolveState(goal, new(IsBlocked: goal.CurrentHold is not null)).ToString();
        if (stage != goal.Status.ToString() && !string.Equals(stage, status, StringComparison.OrdinalIgnoreCase))
            output.WriteLine("Stage: " + stage);
        var role = OwnerConsoleGoalDetail.Stage(goal);
        if (role != "-") output.WriteLine("Role: " + role);
        // A committed-candidate summary owns failure applicability. Retained history alone
        // must not reintroduce a superseded failure as the current landing state.
        if (goal.Status != GoalStatus.Completed && (OwnerConsoleVerifyingGate.Landing(detail, zone) ??
            OwnerConsoleGoalLanding.Line(goal, detail.LandingQueuePosition, zone,
                includeFailureHistory: detail.Changes?.Candidate is null)) is { } landing)
            output.WriteLine("Landing: " + landing);
        if (detail.Gate is null && (goal.Status != GoalStatus.Verifying || detail.GateObserved) &&
            OwnerConsoleLandingBlockerText.Detail(goal, zone) is { } blocker)
            output.WriteLine("Landing blocker: " + blocker);
        if (detail.Gate is { } liveGate) output.WriteLine("Gate: " + OwnerGoalDetailGateLine.Text(liveGate));
        if (detail.Evidence is { } evidence) RenderEvidence(evidence, output);
        if (detail.Changes is { } changes)
            foreach (var line in changes.DetailLines()) output.WriteLine(line);
        output.WriteLine("Tasks:");
        foreach (var task in goal.Tasks) output.WriteLine($"  {task.RequiredRole}: {task.Status}");
        if (goal.CurrentHold is { } hold)
            output.WriteLine("Waiting on: " + OwnerActivityText.WaitingOn(hold.Blocker));
        foreach (var line in OwnerGoalReturnExplanation.Lines(goal, zone)) output.WriteLine(line);
        var lines = new List<(DateTimeOffset Time, string Phrase)>();
        foreach (var item in detail.RecentEvents)
            if (OwnerActivityNarrator.StagePhrase(item, Finding(goal, item)) is { } phrase &&
                (lines.Count == 0 || lines[^1].Phrase != phrase)) lines.Add((item.Timestamp, phrase));
        output.WriteLine(lines.Count == 0 ? "Recent events: none" : "Recent events:");
        foreach (var line in lines.TakeLast(10)) output.WriteLine($"  {line.Time.ToLocalTime():HH:mm:ss} {Plain(line.Phrase)}");
    }

    private static void RenderEvidence(OwnerConsoleEvidenceAttemptProgress progress, IOwnerConsoleOutput output)
    {
        var parts = new List<string>();
        if (progress.Ordinal is { } ordinal) parts.Add(FormattableString.Invariant($"attempt {ordinal}"));
        if (progress.StartedAt is { } start && start <= progress.ObservedAt)
        {
            var elapsed = progress.ObservedAt - start;
            parts.Add(FormattableString.Invariant($"elapsed {Math.Floor(elapsed.TotalMinutes):0}m {elapsed.Seconds}s"));
        }
        if (parts.Count > 0) output.WriteLine("Evidence: " + string.Join(", ", parts));
        if (!string.IsNullOrWhiteSpace(progress.Lane)) output.WriteLine("Running: " + progress.Lane);
        parts.Clear();
        if (progress.Passed is { } passed) parts.Add(FormattableString.Invariant($"{passed} passed"));
        if (progress.Failed is { } failed) parts.Add(FormattableString.Invariant($"{failed} failed"));
        if (parts.Count > 0) output.WriteLine("Tests so far: " + string.Join(", ", parts));
        if (progress.LastProgressAt is { } updated && updated <= progress.ObservedAt)
        {
            var age = progress.ObservedAt - updated;
            output.WriteLine(age.TotalMinutes > ConductorWatchProgressReporter.DefaultStallWarningMinutes
                ? FormattableString.Invariant($"no progress for {Math.Floor(age.TotalMinutes):0} min - possible stall")
                : FormattableString.Invariant($"Updated {Math.Floor(age.TotalSeconds):0} s ago"));
        }
        if (progress.Previous is not { } previous) return;
        parts.Clear();
        if (previous.DurationSeconds is { } seconds) parts.Add(FormattableString.Invariant($"{seconds / 60:0.0} min"));
        if (!string.IsNullOrWhiteSpace(previous.Outcome)) parts.Add(previous.Outcome);
        if (previous.TestsExecuted is { } tests) parts.Add(FormattableString.Invariant($"{tests} tests"));
        if (parts.Count > 0) output.WriteLine("Last attempt: " + string.Join(", ", parts));
    }

    private static string Plain(string text) => text.Replace('{', ' ').Replace('}', ' ').Replace('\r', ' ').Replace('\n', ' ');

    internal static string? Finding(Goal goal, OwnerConductEvent item)
    {
        if (!item.Detail.StartsWith("TaskFailed", StringComparison.Ordinal)) return null;
        var id = OwnerEventFields.Field(item, "task");
        var task = goal.Tasks.FirstOrDefault(task => task.Id.Value == id);
        if (task?.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester)) return null;
        var result = task.VerificationHistory.Where(record => record.CompletedAt <= item.Timestamp)
            .OrderByDescending(record => record.CompletedAt).FirstOrDefault();
        if (result is null) return null;
        var findings = WorkerResultBlockers.TryFindReviewFindingRound(result, out var round, out _) ?
            round.Findings : result.MergedReviewFindings;
        return OwnerHoldReason.FirstLine(findings?.FirstOrDefault()?.Description);
    }
}
