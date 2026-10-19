using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Every fatal failure must name an existing landing path of the same unique member.
internal static class MergeTrainPathAttribution
{
    internal static MergeTrainMemberBinding? TryAttribute(
        IReadOnlyList<string> resultPaths, string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members, out IReadOnlyList<string> evidence)
    {
        evidence = [];
        try
        {
            var results = resultPaths.Select(AcceptanceTrxFailureReader.Read).ToArray();
            if (results.Any(result => result.Status != AcceptanceTrxReadStatus.Readable)) return null;
            var failures = results.SelectMany(result => result.Failures)
                .Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) &&
                    ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is null).ToArray();
            if (failures.Length == 0 || failures.Any(MergeTrainRedAttribution.IsMessageSubjectGuard)) return null;
            if (failures.Any(failure => string.IsNullOrWhiteSpace(
                AcceptanceTestSourceResolver.ExtractClassName(failure.TestName ?? string.Empty)))) return null;

            var candidates = members.Select(member => (Member: member, Paths: member.LandingPaths
                .Select(path => NormalizePath(workspacePath, path))
                .Where(path => path.Contains('/') && File.Exists(Path.Combine(workspacePath, path)))
                .Distinct(StringComparer.Ordinal).ToArray())).ToArray();
            MergeTrainMemberBinding? attributed = null;
            var subjects = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var failure in failures)
            {
                var text = ((failure.Message ?? "") + "\n" + (failure.StackTrace ?? "")).Replace('\\', '/');
                var named = false;
                foreach (var candidate in candidates)
                foreach (var path in candidate.Paths)
                {
                    if (!Names(text, path)) continue;
                    if (attributed is not null && attributed.GoalId != candidate.Member.GoalId) return null;
                    attributed = candidate.Member;
                    named = true;
                    subjects.Add(ClassIdentity(failure.TestName!) + "@" + path);
                }
                if (!named) return null;
            }
            evidence = subjects.ToArray();
            return attributed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Names(string text, string path)
    {
        for (var start = 0; start <= text.Length - path.Length;)
        {
            var index = text.IndexOf(path, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + path.Length;
            if ((index == 0 || !IsPathCharacter(text[index - 1])) &&
                (end == text.Length || (!IsPathCharacter(text[end]) && text[end] != '/'))) return true;
            start = index + 1;
        }
        return false;
    }

    private static bool IsPathCharacter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';

    // Keep the qualified class identity required by the path-evidence contract.
    private static string ClassIdentity(string testName)
    {
        var normalized = testName.Trim();
        var argumentStart = normalized.IndexOf('(');
        if (argumentStart >= 0) normalized = normalized[..argumentStart];
        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var className = AcceptanceTestSourceResolver.ExtractClassName(testName)!;
        return string.Join('.', segments.Take(segments.Length - 2).Append(className));
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
