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
        var recorded = rows.Where(row => row.AttemptId == attemptId && row.Outcome == AcceptanceLaneRerunEvidence.Flake)
            .ToArray();
        var reruns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        return failures.Select(failure =>
        {
            if (failure.Lane is not { } lane || failure.WouldRun != false) return failure;
            if (recorded.Any(row => (row.LaneName == lane || checkNames.Any(check => row.LaneName == check + ": " + lane)) &&
                row.FirstFailingTestIdentities.Any(identity => ImpactReplayTrxEvidence.ResolveClass(identity, classes) == failure.ClassName)))
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
        var firstFailures = new Dictionary<string, int>(StringComparer.Ordinal);
        var passes = new List<(string ClassName, int Ordinal)>();
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
        }
        foreach (var (ordinal, records) in runs)
        {
            foreach (var group in records.GroupBy(record =>
                record.SuitePath.FirstOrDefault(name => classes.Any(item => item.FullName == name)) ??
                ImpactReplayTrxEvidence.ResolveClass(record.Id, classes)))
            {
                if (group.Key is not { } className) continue;
                if (group.Any(record => record.Status is TestResultStatus.Failed or TestResultStatus.Error))
                    firstFailures[className] = Math.Min(firstFailures.GetValueOrDefault(className, int.MaxValue), ordinal);
                else if (!incompleteRuns.Contains(ordinal) && group.Any(record => record.Status == TestResultStatus.Passed))
                    passes.Add((className, ordinal));
            }
        }
        return passes.Where(pass => firstFailures.TryGetValue(pass.ClassName, out var failedAt) && pass.Ordinal > failedAt)
            .Select(pass => pass.ClassName).ToHashSet(StringComparer.Ordinal);
    }
}
