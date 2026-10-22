using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A ratchet subject implicates a member only through unambiguous landing-path ownership.
internal static class MergeTrainRatchetAttribution
{
    internal static MergeTrainMemberBinding? TryAttribute(
        MergeTrainReceipt receipt, string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members, out IReadOnlyList<string> subjects)
    {
        subjects = [];
        if (receipt.Outcome != MergeTrainGateOutcome.Failed ||
            !receipt.FailedChecks.Contains(SourceSizeRatchetPreflight.CheckName, StringComparer.Ordinal)) return null;
        var parsed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in receipt.FailedChecks)
        {
            if (entry == SourceSizeRatchetPreflight.CheckName) continue;
            var separator = entry.IndexOf(" has ", StringComparison.Ordinal);
            if (separator <= 0) continue;
            var subject = entry[..separator];
            if (subject.StartsWith("class:", StringComparison.Ordinal)
                ? Regex.IsMatch(subject[6..], @"^@?[_\p{L}\p{Nl}][_\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}\p{Cf}]*$")
                : subject.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) parsed.Add(subject);
        }
        if (parsed.Count == 0) return null;
        MergeTrainMemberBinding? attributed = null;
        var sourceRequest = new AcceptanceTestSourceResolver.Request(workspacePath);
        foreach (var subject in parsed)
        {
            var sources = (subject.StartsWith("class:", StringComparison.Ordinal)
                    ? sourceRequest.ResolveIdentity(subject[6..] + ".Subject")
                    : new[] { subject })
                .Select(path => NormalizePath(workspacePath, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var owners = members.Where(member => member.LandingPaths
                .Any(path => sources.Contains(NormalizePath(workspacePath, path)))).Distinct().ToArray();
            if (owners.Length != 1 || (attributed is not null && attributed != owners[0])) return null;
            attributed = owners[0];
        }
        subjects = parsed.ToArray();
        return attributed;
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
