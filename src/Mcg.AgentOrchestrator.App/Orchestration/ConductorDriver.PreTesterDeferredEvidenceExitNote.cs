using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private enum PreTesterDeferredEvidenceExit
    {
        PriorOutcomeNotStarted,
        NoResolvedClasses,
        NoNormalizedClasses
    }

    private void RecordPreTesterDeferredEvidenceExit(
        Goal goal,
        TaskSpec developer,
        string testsField,
        PreTesterDeferredEvidenceExit exit,
        string? priorOutcome,
        IReadOnlyList<string> notRun)
    {
        var slug = exit switch
        {
            PreTesterDeferredEvidenceExit.PriorOutcomeNotStarted => "prior-outcome-not-started",
            PreTesterDeferredEvidenceExit.NoResolvedClasses => "no-resolved-classes",
            PreTesterDeferredEvidenceExit.NoNormalizedClasses => "no-normalized-classes",
            _ => throw new InvalidDataException("Unknown pre-Tester deferred-evidence exit.")
        };
        var parsed = DeveloperDeferredTestClassNames.Parse(testsField);
        static string FormatNames(IEnumerable<string> names) =>
            string.Join(",", names.DefaultIfEmpty("<none>"));
        _recordTaskNote(goal.Id, developer.Id,
            $"pre-tester-deferred-evidence skipped: exit={slug} " +
            $"prior={priorOutcome ?? "<none>"} parsed={FormatNames(parsed)} not_run={FormatNames(notRun)}");
    }
}
