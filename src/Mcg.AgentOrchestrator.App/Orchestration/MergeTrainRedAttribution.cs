using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Attribution describes only declared test-source ownership, never a guessed cause.
internal static class MergeTrainRedAttribution
{
    internal static bool IsGenuineTrainRed(MergeTrainReceipt receipt, MergeTrainMemberBinding? attributed = null)
    {
        if (receipt.Outcome != MergeTrainGateOutcome.Failed) return false;
        if (receipt.FailedChecks.Contains(SourceSizeRatchetPreflight.CheckName, StringComparer.Ordinal))
            return true;

        // An absent apparatus signature alone is not failure evidence. Require a fatal test
        // result; unreadable/missing TRX and all-apparatus failures cannot implicate a tree.
        var failures = ReadFatalFailures(receipt.GateTestResultPaths);
        return failures.Count != 0 &&
            (!failures.Any(IsMessageSubjectGuard) || attributed is not null);
    }

    internal static IReadOnlyList<AcceptanceTrxFailure> ReadFatalFailures(IReadOnlyList<string> paths) =>
        FatalFailures(paths.Select(AcceptanceTrxFailureReader.Read));

    private static IReadOnlyList<AcceptanceTrxFailure> FatalFailures(IEnumerable<AcceptanceTrxReadResult> results) =>
        results.Where(result => result.Status == AcceptanceTrxReadStatus.Readable)
            .SelectMany(result => result.Failures)
            .Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) &&
                ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is null)
            .ToArray();

    internal static MergeTrainMemberBinding? TryAttribute(
        MergeTrainReceipt receipt,
        string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members) =>
        TryAttribute(receipt, workspacePath, members, out _);

    internal static bool IsMessageSubjectGuard(AcceptanceTrxFailure failure)
    {
        var identity = failure.TestName ?? string.Empty;
        var className = AcceptanceTestSourceResolver.ExtractClassName(identity);
        return (className == "AcceptanceGateEngineSettingsTests" &&
                identity.EndsWith(".AcceptanceGateEngineDisabledCollectionsSpanningLanesShareAnExclusiveResource", StringComparison.Ordinal)) ||
            (className == "WorkflowDecisionCoverageRatchetTests" &&
                identity.EndsWith(".EveryUndecidedSite_IsAllowListedByFileAndMember", StringComparison.Ordinal));
    }

    internal static MergeTrainMemberBinding? TryAttribute(
        MergeTrainReceipt receipt,
        string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members,
        out IReadOnlyList<string> failingSubjects)
    {
        failingSubjects = [];
        if (receipt.Outcome != MergeTrainGateOutcome.Failed) return null;
        return TryAttribute(receipt.GateTestResultPaths, workspacePath, members, out failingSubjects);
    }

    internal static MergeTrainMemberBinding? TryAttribute(
        IReadOnlyList<string> resultPaths,
        string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members,
        out IReadOnlyList<string> failingSubjects)
    {
        failingSubjects = [];
        var results = resultPaths.Select(AcceptanceTrxFailureReader.Read).ToArray();
        // Missing evidence may hide another owner's failure; never attribute a partial read.
        if (results.Any(result => result.Status != AcceptanceTrxReadStatus.Readable)) return null;
        var failures = FatalFailures(results);
        if (failures.Count == 0) return null;

        MergeTrainMemberBinding? attributed = null;
        var sourcesByClass = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var failure in failures)
        {
            // These guards report another artifact's defect. Their own source is not the
            // subject, even when an unrelated train member happens to own the guard file.
            if (IsMessageSubjectGuard(failure))
            {
                var messageSubjects = ReadMessageSubjects(failure);
                if (messageSubjects.Count == 0) return null;
                foreach (var subject in messageSubjects)
                {
                    var subjectSources = subject.EndsWith(".cs", StringComparison.Ordinal)
                        ? members.SelectMany(member => member.LandingPaths)
                            .Select(path => NormalizePath(workspacePath, path))
                            .Where(path => subject.Contains('/')
                                ? path.Equals(subject, StringComparison.OrdinalIgnoreCase)
                                : Path.GetFileName(path).Equals(subject, StringComparison.OrdinalIgnoreCase))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : AcceptanceTestSourceResolver.ResolveSourcePaths(workspacePath, null, subject + ".Subject")
                            .Select(path => NormalizePath(workspacePath, path))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!TryOwn(subjectSources)) return null;
                    subjects.Add(subject);
                }
                continue;
            }
            var className = AcceptanceTestSourceResolver.ExtractClassName(failure.TestName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(className)) return null;
            if (!sourcesByClass.TryGetValue(className, out var sources))
            {
                sources = AcceptanceTestSourceResolver.ResolveSourcePaths(workspacePath, null, failure.TestName)
                    .Select(path => NormalizePath(workspacePath, path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                sourcesByClass.Add(className, sources);
            }
            if (!TryOwn(sources)) return null;
            subjects.Add(className);
        }
        failingSubjects = subjects.Order(StringComparer.Ordinal).ToArray();
        return attributed;

        bool TryOwn(HashSet<string> sources)
        {
            if (sources.Count == 0) return false;
            var owners = members.Where(member => member.LandingPaths
                .Any(path => sources.Contains(NormalizePath(workspacePath, path)))).ToArray();
            if (owners.Length != 1 || (attributed is not null && attributed.GoalId != owners[0].GoalId))
                return false;
            attributed = owners[0];
            return true;
        }
    }

    private static IReadOnlyList<string> ReadMessageSubjects(AcceptanceTrxFailure failure)
    {
        var message = failure.Message ?? string.Empty;
        if (AcceptanceTestSourceResolver.ExtractClassName(failure.TestName!) == "AcceptanceGateEngineSettingsTests")
        {
            // The receipt lists unchanged collection peers as context. Only mappings to
            // the catch-all Remainder lane identify the missing explicit lane membership.
            // Xunit appends Expected/Actual lines to the guard's assertion message.
            var firstLine = message.Split('\n', 2)[0].TrimEnd('\r');
            var mappings = Regex.Match(firstLine,
                @"^Disabled collection '[^']+' spans acceptance lanes \[[^\]]+\] without a shared exclusive resource key\. Mapped classes: \[(?<mappings>[^\]]+)\]\.$");
            if (!mappings.Success) return [];
            var subjects = new List<string>();
            foreach (var entry in mappings.Groups["mappings"].Value.Split(','))
            {
                var mapping = Regex.Match(entry.Trim(), @"^(?<class>[A-Za-z_][\w.]*) -> (?<lane>.+)$");
                if (!mapping.Success) return [];
                if (mapping.Groups["lane"].Value == "Remainder") subjects.Add(mapping.Groups["class"].Value);
            }
            return subjects;
        }

        const string prefix = "Unlisted undecided sites:";
        if (!message.StartsWith(prefix, StringComparison.Ordinal)) return [];
        var files = new List<string>();
        foreach (var line in message[prefix.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var site = Regex.Match(line.Trim(), @"^(?<file>[A-Za-z_][\w./\\-]*\.cs) : [A-Za-z_][\w]*$");
            if (!site.Success) return [];
            files.Add(site.Groups["file"].Value.Replace('\\', '/'));
        }
        return files;
    }

    private static string NormalizePath(string workspacePath, string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
            normalized = Path.GetRelativePath(workspacePath, normalized).Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}
