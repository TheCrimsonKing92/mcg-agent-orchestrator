using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal static string FormatStalledBlockerDetail(string blocker)
    {
        if (!blocker.StartsWith(ConductorDriver.NoReadyBatchHoldPrefix, StringComparison.Ordinal))
        {
            return SanitizeReason(blocker);
        }

        var reasonEnd = ConductorDriver.FindFirstBlockerReasonEnd(blocker);
        if (reasonEnd < 0)
        {
            return SanitizeReason(blocker);
        }

        // Whitespace substitution preserves positions found in the original hold text.
        var sanitized = blocker.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');
        var firstReason = sanitized[..reasonEnd];
        var remainder = sanitized[reasonEnd..];
        const int remainderLimit = 512;
        if (remainder.Length <= remainderLimit)
        {
            return sanitized;
        }

        const string separator = ";_task_";
        var starts = new List<int>();
        for (var index = remainder.IndexOf(separator, StringComparison.Ordinal);
             index >= 0;
             index = remainder.IndexOf(separator, index + separator.Length, StringComparison.Ordinal))
        {
            starts.Add(index);
        }

        if (starts.Count == 0)
        {
            return firstReason + remainder[..(remainderLimit - 3)] + "...";
        }

        var tail = new StringBuilder(remainder[..starts[0]]);
        var included = 0;
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : remainder.Length;
            var entry = remainder[starts[i]..end];
            var omittedAfter = starts.Count - i - 1;
            var marker = omittedAfter > 0 ? OmittedBlockerMarker(omittedAfter) : string.Empty;
            if (tail.Length + entry.Length + marker.Length > remainderLimit)
            {
                break;
            }

            tail.Append(entry);
            included++;
        }

        var omitted = starts.Count - included;
        if (omitted > 0)
        {
            tail.Append(OmittedBlockerMarker(omitted));
        }

        return firstReason + tail;
    }

    private static string OmittedBlockerMarker(int count) =>
        count == 1 ? "..._(+1_more_blocker)" : $"..._(+{count}_more_blockers)";
}
