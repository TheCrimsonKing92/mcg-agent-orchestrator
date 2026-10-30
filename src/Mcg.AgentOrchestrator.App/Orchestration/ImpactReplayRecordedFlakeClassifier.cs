using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplayRecordedFlakeClassifier
{
    internal static IReadOnlyList<ImpactReplayFailingClass> Classify(string attemptId,
        IReadOnlyList<ImpactReplayFailingClass> failures, IReadOnlyList<ImpactReplayLaneOutcome> lanes,
        IReadOnlyList<string> checkNames, IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        IReadOnlyList<AcceptanceLaneFlakeRow> rows)
    {
        var recorded = rows.Where(row => row.AttemptId == attemptId).ToArray();
        var reruns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        return failures.Select(failure =>
        {
            if (failure.Lane is not { } lane || failure.WouldRun != false) return failure;
            var matchingRows = recorded.Where(row =>
                (row.LaneName == lane || checkNames.Any(check => row.LaneName == check + ": " + lane)) &&
                row.FirstFailingTestIdentities.Any(identity =>
                    ImpactReplayTrxEvidence.ResolveClass(identity, classes) == failure.ClassName)).ToArray();
            // An adjudicated failure must not be overturned by partial TRX evidence.
            if (matchingRows.Any(row => row.Outcome == AcceptanceLaneRerunEvidence.ConfirmedFailure)) return failure;
            if (matchingRows.Any(row => row.Outcome == AcceptanceLaneRerunEvidence.Flake))
                return failure with { FlakeEvidence = "lane-flakes" };

            if (!reruns.TryGetValue(lane, out var flakyClasses))
            {
                flakyClasses = ReadReruns(lanes.Single(item => item.Lane == lane).TrxPaths, classes);
                reruns.Add(lane, flakyClasses);
            }
            return flakyClasses.Contains(failure.ClassName) ? failure with { FlakeEvidence = "rerun" } : failure;
        }).ToArray();
    }

    private static HashSet<string> ReadReruns(IReadOnlyList<string> paths,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        var runs = new Dictionary<int, List<TestResult>>();
        var incompleteRuns = new HashSet<int>();
        foreach (var path in paths)
        {
            var suffix = Regex.Match(Path.GetFileNameWithoutExtension(path), @"-run-(\d+)$", RegexOptions.CultureInvariant);
            var ordinal = 0;
            if (suffix.Success && !int.TryParse(suffix.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal))
                continue;
            var result = new TrxTestResultSource().Read(path);
            if (result.Status != TestResultReadStatus.Readable)
            {
                incompleteRuns.Add(ordinal);
                continue;
            }
            if (!runs.TryGetValue(ordinal, out var records)) runs.Add(ordinal, records = []);
            records.AddRange(result.Records);
            var completion = AcceptanceShardCompletionAdjudicator.InspectTrxCompletion([path]);
            if (!completion.Passed || completion.FailedPredicate is not null ||
                completion.ExecutedTestCount is not > 0 || completion.DiscoveredTestCount is null ||
                completion.ExecutedTestCount < completion.DiscoveredTestCount || completion.NotExecutedTestCount != 0 ||
                !(completion.Outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase) ||
                  completion.Outcome.Equals("Completed", StringComparison.OrdinalIgnoreCase)))
                incompleteRuns.Add(ordinal);
        }
        var failures = runs.SelectMany(run => run.Value
                .Where(record => record.Status is TestResultStatus.Failed or TestResultStatus.Error)
                .Select(record => (ClassName: ClassName(record), record.Id, Ordinal: run.Key)))
            .Where(failure => failure.ClassName is not null).GroupBy(failure => failure.ClassName!);
        var flakyClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in failures)
        {
            var lastFailure = group.Max(failure => failure.Ordinal);
            var failedIdentities = group.Select(failure => failure.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (runs.Any(run => run.Key > lastFailure && !incompleteRuns.Contains(run.Key) &&
                failedIdentities.All(identity => run.Value.Any(record => record.Id == identity &&
                    ClassName(record) == group.Key && record.Status == TestResultStatus.Passed)) &&
                run.Value.Where(record => ClassName(record) == group.Key)
                    .All(record => record.Status == TestResultStatus.Passed)))
                flakyClasses.Add(group.Key);
        }
        return flakyClasses;

        string? ClassName(TestResult record) =>
            record.SuitePath.FirstOrDefault(name => classes.Any(item => item.FullName == name)) ??
            ImpactReplayTrxEvidence.ResolveClass(record.Id, classes);
    }
}
