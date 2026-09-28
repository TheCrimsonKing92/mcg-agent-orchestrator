using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private enum PreTesterDeferredEvidenceDeclineClause
    {
        None,
        RunnerNotConfigured,
        TestsFieldMissing,
        TestsStatusNotDeferred,
        InvalidCandidate,
        InvalidBaseCommit,
        BaseEqualsCandidate
    }

    private PreTesterDeferredEvidenceDeclineClause EvaluatePreTesterDeferredEvidenceGuard(
        TaskSpec developer, string? candidateSha, out string testsField)
    {
        // Parse first so even a missing runner can name its first failing clause for a recorded result.
        var hasTestsField = WorkerResultBlockers.TryFindTests(developer.LastVerification, out testsField);
        var hasDeferredStatus = WorkerResultBlockers.TryGetTestsStatus(
            developer.LastVerification, out var status) &&
            status == WorkerResultBlockers.TestsStatus.Deferred;
        var baseCommit = developer.LastDispatch?.BaseCommit;

        if (!_focusedEvidenceRunnerConfigured)
            return PreTesterDeferredEvidenceDeclineClause.RunnerNotConfigured;
        if (!hasTestsField)
            return PreTesterDeferredEvidenceDeclineClause.TestsFieldMissing;
        if (!hasDeferredStatus)
            return PreTesterDeferredEvidenceDeclineClause.TestsStatusNotDeferred;
        if (!ConductorGitRevisionReader.IsValid(candidateSha))
            return PreTesterDeferredEvidenceDeclineClause.InvalidCandidate;
        if (!ConductorGitRevisionReader.IsValid(baseCommit))
            return PreTesterDeferredEvidenceDeclineClause.InvalidBaseCommit;
        if (string.Equals(candidateSha, baseCommit, StringComparison.OrdinalIgnoreCase))
            return PreTesterDeferredEvidenceDeclineClause.BaseEqualsCandidate;
        return PreTesterDeferredEvidenceDeclineClause.None;
    }

    private void RecordPreTesterDeferredEvidenceDecline(
        Goal goal, TaskSpec developer, PreTesterDeferredEvidenceDeclineClause clause, string? candidateSha)
    {
        if (developer.LastDispatch is null ||
            developer.LastVerification is not { WorkerResultPresent: true })
            return;

        var slug = clause switch
        {
            PreTesterDeferredEvidenceDeclineClause.RunnerNotConfigured => "runner-not-configured",
            PreTesterDeferredEvidenceDeclineClause.TestsFieldMissing => "tests-field-missing",
            PreTesterDeferredEvidenceDeclineClause.TestsStatusNotDeferred => "tests-status-not-deferred",
            PreTesterDeferredEvidenceDeclineClause.InvalidCandidate => "invalid-candidate",
            PreTesterDeferredEvidenceDeclineClause.InvalidBaseCommit => "invalid-base-commit",
            PreTesterDeferredEvidenceDeclineClause.BaseEqualsCandidate => "base-equals-candidate",
            _ => throw new InvalidDataException("A pre-Tester decline must have a failing clause.")
        };
        _recordTaskNote(goal.Id, developer.Id,
            $"pre-tester-deferred-evidence declined: clause={slug} " +
            $"candidate={FormatPreTesterGuardValue(candidateSha)} " +
            $"base={FormatPreTesterGuardValue(developer.LastDispatch.BaseCommit)}");
    }

    private static string FormatPreTesterGuardValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<none>";
        var trimmed = value.Trim();
        return new string(trimmed.Take(64)
            .Select(character => char.IsWhiteSpace(character) ? '_' : character)
            .ToArray());
    }
}
