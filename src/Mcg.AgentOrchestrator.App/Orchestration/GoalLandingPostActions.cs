using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalLandingPostActions
{
    public static DogfoodLogRecord RecordDogfoodEntry(
        Goal goal,
        string executionDirectory,
        string dogfoodLogStorePath,
        Action<string>? writeLine = null)
    {
        DogfoodLandingEvidence evidence;
        try
        {
            evidence = ResolveDogfoodLandingEvidence(
                GoalOperationJournal.Read(executionDirectory, goal.Id));
        }
        catch (Exception ex)
        {
            evidence = DogfoodLandingEvidence.Unknown;
            writeLine?.Invoke(
                $"Dogfood acceptance evidence unavailable for goal {goal.Id.Value[..8]} ({ex.GetType().Name}); recording unknown status.");
        }

        var entry = DogfoodLogRenderer.Render(goal, evidence);
        return new DogfoodLogStore(dogfoodLogStorePath)
            .UpsertAsync(new DogfoodLogAppend(
                goal.Id.Value,
                entry.Header,
                entry.Summary,
                entry.OperatorGate,
                entry.ModelFit,
                entry.Render()))
            .GetAwaiter()
            .GetResult();
    }

    internal static DogfoodLandingEvidence ResolveDogfoodLandingEvidence(
        GoalOperationJournalSummary journal)
    {
        var manualLanding =
            string.Equals(
                GoalOperationJournal.TryGetLatestLandingIntent(journal)?.Source,
                "goal-mark-landed",
                StringComparison.OrdinalIgnoreCase);

        var acceptance = journal.Entries
            .Select((entry, index) => (Entry: entry, Index: index))
            .Where(item => !string.IsNullOrWhiteSpace(item.Entry.AcceptanceOutcome))
            .OrderByDescending(item => item.Entry.At)
            .ThenByDescending(item => item.Index)
            .Select(item => item.Entry)
            .FirstOrDefault();

        if (acceptance is null)
        {
            return new DogfoodLandingEvidence(
                DogfoodAcceptanceDisposition.Unknown,
                WasManuallyLanded: manualLanding);
        }

        var outcome = acceptance.AcceptanceOutcome!.Trim();
        var disposition = (acceptance.Status, outcome.ToLowerInvariant()) switch
        {
            (GoalOperationStatus.Completed, "passed" or "gate-passed") =>
                DogfoodAcceptanceDisposition.Passed,
            (GoalOperationStatus.Failed, "failed") =>
                DogfoodAcceptanceDisposition.Failed,
            _ => DogfoodAcceptanceDisposition.Inconclusive
        };

        return new DogfoodLandingEvidence(disposition, outcome, manualLanding);
    }

    public static bool AutoCloseSourceBacklogItem(
        Goal? goal,
        string backlogStorePath,
        Action<string>? writeLine = null,
        AgentOrchestratorKernel? kernel = null,
        string? executionDirectory = null,
        string? integrateCommitSha = null,
        string? stateDbPath = null)
    {
        if (goal?.SourceBacklogItemId is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(stateDbPath))
        {
            if (kernel is null)
                throw new ArgumentException("An authoritative source-claim check requires the current kernel.", nameof(kernel));
            SourceBacklogClaimSnapshot? claim;
            try
            {
                claim = new SourceBacklogClaimStore(stateDbPath)
                    .ResolveClaim(kernel, goal.SourceBacklogItemId);
            }
            catch (LegacySourceBacklogOwnerAmbiguousException ex)
            {
                var warning = $"Warning: goal {goal.Id.Value[..8]} landed, but linked backlog item {goal.SourceBacklogItemId} was not closed: {ex.Message}";
                kernel.RecordGoalPolicyDecision(goal.Id, warning);
                writeLine?.Invoke(warning);
                return false;
            }
            if (claim is null || !string.Equals(claim.OwnerGoalId, goal.Id.Value, StringComparison.Ordinal))
            {
                writeLine?.Invoke(
                    $"Backlog item {goal.SourceBacklogItemId} was not closed because goal {goal.Id.Value[..8]} is historical; authoritative owner is {claim?.OwnerGoalId ?? "none"}.");
                return false;
            }
        }

        try
        {
            var store = new BacklogStore(backlogStorePath);
            var commitSha = string.IsNullOrWhiteSpace(integrateCommitSha)
                ? TryResolveHeadCommit(executionDirectory) ?? "unknown"
                : integrateCommitSha.Trim();
            if (goal.SourceBacklogCoverage == SourceBacklogCoverage.Slice)
            {
                var landingNote = $"Recorded slice landing for goal {goal.Id.Value}. integrateCommit={commitSha}";
                var landingResult = store.TryRecordOpenItemLandingAsync(goal.SourceBacklogItemId, landingNote).GetAwaiter().GetResult();
                switch (landingResult.Disposition)
                {
                    case BacklogLandingDisposition.Recorded:
                        writeLine?.Invoke($"Recorded slice landing on backlog item {goal.SourceBacklogItemId} (goal {goal.Id.Value[..8]}, commit {ShortSha(commitSha)}); item remains Open.");
                        return false;
                    case BacklogLandingDisposition.AlreadyRecorded:
                        writeLine?.Invoke($"Slice landing already recorded on backlog item {goal.SourceBacklogItemId} (goal {goal.Id.Value[..8]}, no-op).");
                        return false;
                    case BacklogLandingDisposition.AlreadyDone:
                        writeLine?.Invoke($"Backlog item {goal.SourceBacklogItemId} already closed (goal {goal.Id.Value[..8]} slice landed, no-op).");
                        return false;
                    case BacklogLandingDisposition.NotFound:
                        var sliceWarning = $"Warning: goal {goal.Id.Value[..8]} landed, but linked backlog item {goal.SourceBacklogItemId} was not found; slice landing was not recorded.";
                        kernel?.RecordGoalPolicyDecision(goal.Id, sliceWarning);
                        writeLine?.Invoke(sliceWarning);
                        return false;
                    default:
                        return false;
                }
            }

            var note = $"Auto-closed after goal {goal.Id.Value} landed. integrateCommit={commitSha}";
            var result = store.TryCloseByIdWithResultAsync(goal.SourceBacklogItemId, note: note).GetAwaiter().GetResult();
            switch (result.Disposition)
            {
                case BacklogCloseDisposition.Closed:
                    writeLine?.Invoke($"Closed backlog item {goal.SourceBacklogItemId} (goal {goal.Id.Value[..8]} landed, commit {ShortSha(commitSha)}).");
                    return true;
                case BacklogCloseDisposition.AlreadyDone:
                    writeLine?.Invoke($"Backlog item {goal.SourceBacklogItemId} already closed (goal {goal.Id.Value[..8]} landed, no-op).");
                    return false;
                case BacklogCloseDisposition.NotFound:
                    var warning = $"Warning: goal {goal.Id.Value[..8]} landed, but linked backlog item {goal.SourceBacklogItemId} was not found; backlog close skipped.";
                    kernel?.RecordGoalPolicyDecision(goal.Id, warning);
                    writeLine?.Invoke(warning);
                    return false;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            var failure = goal.SourceBacklogCoverage == SourceBacklogCoverage.Slice
                ? "slice landing was not recorded"
                : "was not closed";
            var warning = $"Warning: goal {goal.Id.Value[..8]} landed, but linked backlog item {goal.SourceBacklogItemId} {failure}: {ex.Message}";
            kernel?.RecordGoalPolicyDecision(goal.Id, warning);
            writeLine?.Invoke(warning);
            return false;
        }
    }

    private static string? TryResolveHeadCommit(string? executionDirectory)
    {
        if (string.IsNullOrWhiteSpace(executionDirectory))
        {
            return null;
        }

        try
        {
            var result = GitCli.Run(executionDirectory, "rev-parse", "HEAD");
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output)
                ? result.Output.Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ShortSha(string sha) => sha.Length <= 12 ? sha : sha[..12];
}
