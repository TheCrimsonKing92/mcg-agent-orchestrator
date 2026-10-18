using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal TestImpactHeadroomWarning? HeadroomWarning { get; set; }

    private void RecordTestImpactEvents(Goal goal, string goalPrefix, PreReviewEvidenceContext context)
    {
        TryRecordTestImpactDegradedEvent(goal, goalPrefix, context);
        TryRecordTestImpactHeadroomLowEvent(goal, goalPrefix, context);
    }

    private void TryRecordTestImpactHeadroomLowEvent(
        Goal goal, string goalPrefix, PreReviewEvidenceContext context)
    {
        lock (_testImpactDegradedEventLock)
        {
            string? mainSha;
            try { mainSha = ResolveCurrentMainHeadSha(goal); }
            catch { mainSha = null; }
            (HeadroomWarning ??= new(() => _cohortWorkspace?.ExecutionDirectory,
                RepositoryTestImpactClosure.DefaultIndexedSourceFileCap)).TryRecord(
                goal.Id.Value, goalPrefix, context, mainSha, () => _testImpactDegradedEventWriter ??=
                    _cohortWorkspace is null ? null : new ConductEventLogWriter(_cohortWorkspace.ConductEventsLogPath));
        }
    }
}
