using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplayTrxEvidence
{
    internal static (IReadOnlyList<ImpactReplayLaneOutcome> LaneOutcomes,
        IReadOnlyList<ImpactReplayFailingClass> Failures, IReadOnlyList<string> Warnings) Read(
        ImpactReplayStoredAttempt attempt, IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<string> checkNames, IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        var pathsByLane = lanes.ToDictionary(lane => lane.Name, _ => new List<string>(), StringComparer.Ordinal);
        var failures = new List<ImpactReplayFailingClass>();
        var warnings = new List<string>();
        var outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawPath in attempt.TestResultPaths)
        {
            var path = Path.GetFullPath(rawPath.Replace('\\', Path.DirectorySeparatorChar), attempt.GoalDirectory);
            var stem = Path.GetFileNameWithoutExtension(path);
            var matching = lanes.Where(lane => checkNames.Any(check => Regex.IsMatch(stem,
                    @"(?:^|\.)" + Regex.Escape(AcceptanceArtifactFileNames.Slug(check + ": " + lane.Name)) + @"(?:-run-\d+)?$",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)))
                .Select(lane => lane.Name).ToArray();
            foreach (var lane in matching) pathsByLane[lane].Add(path);
            var read = AcceptanceTrxFailureReader.Read(path);
            if (read.Status != AcceptanceTrxReadStatus.Readable)
            {
                warnings.Add($"TRX {read.Status}: {path}");
                foreach (var lane in matching) outcomes[lane] = "Incomplete";
                continue;
            }
            var fatal = read.Failures.Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome)).ToArray();
            foreach (var lane in matching)
            {
                if (fatal.Length > 0) outcomes[lane] = "Failed";
                else outcomes.TryAdd(lane, "Passed");
            }
            foreach (var failure in fatal)
            {
                var className = ResolveClass(failure.TestName, classes);
                if (className is null)
                {
                    warnings.Add($"Failing test class could not be resolved: {path}: {failure.TestName ?? "unknown test"}");
                    failures.Add(new(failure.TestName ?? "unknown test", null, null));
                    continue;
                }
                // The TRX's lane identifies where the failure actually happened, including overlapping filters.
                if (matching.Length == 1) failures.Add(new(className, matching[0], null));
                else
                {
                    warnings.Add($"Failing class has no unambiguous lane receipt: {className}: {path}");
                    failures.Add(new(className, null, null));
                }
            }
        }
        foreach (var lane in lanes)
        {
            if (!checkNames.Any(check => attempt.FailedChecks.Contains(check + ": " + lane.Name, StringComparer.Ordinal))) continue;
            outcomes[lane.Name] = "Failed";
            if (pathsByLane[lane.Name].Count == 0) warnings.Add($"Failed lane has no TRX: {lane.Name}");
        }
        var ran = lanes.Where(lane => pathsByLane[lane.Name].Count > 0 || outcomes.ContainsKey(lane.Name))
            .Select(lane => new ImpactReplayLaneOutcome(lane.Name, outcomes.GetValueOrDefault(lane.Name, "Incomplete"),
                pathsByLane[lane.Name])).ToArray();
        if (ran.Length > 0 && ran.All(lane => lane.TrxPaths.Count == 0)) warnings.Add("No lane TRX evidence was available.");
        return (ran, failures.Distinct().ToArray(), warnings);
    }

    internal static string? ResolveClass(string? identity, IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        var normalized = AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
        var known = classes.Where(item => normalized.StartsWith(item.FullName + ".", StringComparison.Ordinal) ||
                                         normalized.StartsWith(item.FullName + "+", StringComparison.Ordinal))
            .OrderByDescending(item => item.FullName.Length).FirstOrDefault();
        if (known is not null) return known.FullName;
        var methodStart = normalized.LastIndexOf('.');
        return methodStart > 0 ? normalized[..methodStart] : null;
    }
}
