using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly HashSet<(string Bound, bool IsMain, string Sha)> _testImpactHeadroomCommits = [];

    private void RecordTestImpactEvents(Goal goal, string goalPrefix, PreReviewEvidenceContext context)
    {
        TryRecordTestImpactDegradedEvent(goal, goalPrefix, context);
        TryRecordTestImpactHeadroomLowEvent(goal, goalPrefix, context);
    }

    private void TryRecordTestImpactHeadroomLowEvent(
        Goal goal, string goalPrefix, PreReviewEvidenceContext context)
    {
        if (context.TestImpactHeadroom is not { } headroom) return;

        lock (_testImpactDegradedEventLock)
        {
            string? mainSha;
            try { mainSha = ResolveCurrentMainHeadSha(goal); }
            catch { mainSha = null; }
            var hasMain = !string.IsNullOrWhiteSpace(mainSha);
            var sha = hasMain ? mainSha! : context.CandidateSha!;
            (string Kind, int? Count, int Cap)[] bounds =
            [
                ("IndexedSourceFiles", headroom.IndexedSourceFileCount, headroom.IndexedSourceFileCap),
                ("SelectedTestClasses", headroom.SelectedTestClassCount, headroom.SelectedTestClassCap),
                ("FrontierSymbols", headroom.LargestFrontierSymbolCount, headroom.FrontierSymbolCap)
            ];
            foreach (var bound in bounds)
            {
                if (bound.Count is not { } count || (long)count * 5 < (long)bound.Cap * 4) continue;
                var key = (bound.Kind, hasMain, sha);
                if (_testImpactHeadroomCommits.Contains(key)) continue;
                try
                {
                    if (_testImpactDegradedEventWriter is null && _cohortWorkspace is not null)
                        _testImpactDegradedEventWriter = new ConductEventLogWriter(_cohortWorkspace.ConductEventsLogPath);
                    if (_testImpactDegradedEventWriter is null) continue;
                    _testImpactDegradedEventWriter.Append(
                        "test-impact-headroom-low", goal.Id.Value,
                        $"TEST_IMPACT_HEADROOM_LOW goal={goalPrefix} candidate={context.CandidateSha} " +
                        $"main={(hasMain ? mainSha : "unresolved")} bound={bound.Kind} count={count} cap={bound.Cap}");
                    _testImpactHeadroomCommits.Add(key);
                }
                catch
                {
                    // Diagnostic delivery cannot block review; an unsuccessful append may retry.
                }
            }
        }
    }
}
