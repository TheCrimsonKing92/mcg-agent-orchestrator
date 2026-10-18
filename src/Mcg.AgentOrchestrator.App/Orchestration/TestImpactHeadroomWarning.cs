using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class TestImpactHeadroomWarning(Func<string?> measurementRoot, int indexedSourceFileCap)
{
    private readonly HashSet<(string Bound, bool IsMain, string Sha)> _emitted = [];
    private readonly Dictionary<(bool IsMain, string Sha), RepositoryTestImpactHeadroom?> _measurements = [];

    internal int MeasurementCount { get; private set; }

    internal void TryRecord(string goalId, string goalPrefix, PreReviewEvidenceContext context,
        string? mainSha, Func<ConductEventLogWriter?> resolveWriter)
    {
        var hasMain = !string.IsNullOrWhiteSpace(mainSha);
        var sha = hasMain ? mainSha! : context.CandidateSha!;
        var planned = context.TestImpactHeadroom;
        var indexed = planned?.IndexedSourceFileCount is not null
            ? planned : Measure(hasMain, hasMain ? mainSha! : "");
        (string Kind, int? Count, int Cap)[] bounds =
        [
            ("IndexedSourceFiles", indexed?.IndexedSourceFileCount, indexed?.IndexedSourceFileCap ?? indexedSourceFileCap),
            ("SelectedTestClasses", planned?.SelectedTestClassCount, planned?.SelectedTestClassCap ?? 0),
            ("FrontierSymbols", planned?.LargestFrontierSymbolCount, planned?.FrontierSymbolCap ?? 0)
        ];
        foreach (var bound in bounds)
        {
            if (bound.Count is not { } count || (long)count * 5 < (long)bound.Cap * 4) continue;
            var key = (bound.Kind, hasMain, sha);
            if (_emitted.Contains(key)) continue;
            try
            {
                var writer = resolveWriter();
                if (writer is null) continue;
                writer.Append("test-impact-headroom-low", goalId,
                    $"TEST_IMPACT_HEADROOM_LOW goal={goalPrefix} candidate={context.CandidateSha} " +
                    $"main={(hasMain ? mainSha : "unresolved")} bound={bound.Kind} count={count} cap={bound.Cap}");
                _emitted.Add(key);
            }
            catch
            {
                // Diagnostic delivery cannot block review; an unsuccessful append may retry.
            }
        }
    }

    private RepositoryTestImpactHeadroom? Measure(bool hasMain, string sha)
    {
        var key = (hasMain, sha);
        if (_measurements.TryGetValue(key, out var measured)) return measured;
        try
        {
            var root = measurementRoot();
            if (string.IsNullOrWhiteSpace(root)) return null;
            MeasurementCount++;
            measured = RepositoryTestImpactClosure.MeasureIndexedSources(root, indexedSourceFileCap);
        }
        catch
        {
            // Advisory measurement must not change dispatch behavior, even for a broken root delegate.
        }
        _measurements.Add(key, measured);
        return measured;
    }
}
