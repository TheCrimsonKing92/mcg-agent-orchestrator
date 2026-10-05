using Mcg.AgentOrchestrator.Core;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerContextProjectionResidual
{
    internal const string IngressVersion = "worker-context-legacy-marked-text-ingress-v1";

    // Retirement requires both: (1) a source census proving no live producer under src/ emits
    // WorkerContextProjectionBoundary markers outside the legacy projection entry point, and
    // (2) zero persisted occurrences of the typed progress line
    // "legacy-marked-text-ingress-v1 goal=<goalId> artifact=<identity>" for every goal that was
    // non-terminal when goal 29423867 landed. Existing in-flight worktrees remain readable until then.
    internal static LegacyMarkedTextProjection ParseLegacyMarkedTextV1(
        string content,
        AgentRole targetRole,
        string? goalId = null,
        Action<string>? progressRecorder = null,
        bool removeReviewerChangedPaths = false,
        bool removeReviewerConflictPaths = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        var instructions = FindBriefHeading(content, "## Instructions", 0);
        if (instructions < 0)
        {
            throw new InvalidOperationException("Typed context brief is missing its Instructions source boundary.");
        }

        var identities = new List<LogicalArtifactIdentity>();
        var current = RemoveProjectionBlocks(content[instructions..], identities).Trim();
        if (targetRole == AgentRole.Reviewer)
        {
            current = WorkerContextRenderer.ApplyReviewerPreviewPolicy(
                current,
                removeReviewerChangedPaths,
                removeReviewerConflictPaths);
        }

        var header = RestoreLiterals(content[..instructions]);
        header = RemoveMarkedBriefBlock(
            header,
            "<!-- ACCUMULATED_RETRY_FEEDBACK_START -->",
            "<!-- ACCUMULATED_RETRY_FEEDBACK_END -->");
        header = RemoveMarkedBriefBlock(
            header,
            "<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_START -->",
            "<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_END -->");
        header = RemoveMarkedBriefBlock(
            header,
            "<!-- ACCEPTANCE_FAILURE_START -->",
            "<!-- ACCEPTANCE_FAILURE_END -->");
        header = RemoveLineRange(header, "Goal: ", "Goal id: ");
        header = RemoveLineRange(header, "Task: ", "Task role: ");
        foreach (var prefix in new[]
        {
            "# Agent Task Brief",
            "Goal id: ",
            "Goal status: ",
            "Task role: ",
            "Task status: ",
            "Task id: ",
            "Working directory, use absolute paths: ",
            "Context files: read "
        })
        {
            header = RemoveLineWithPrefix(header, prefix);
        }

        if (progressRecorder is not null)
        {
            foreach (var identity in identities)
            {
                progressRecorder(
                    $"{IngressVersion} goal={goalId ?? "unknown"} artifact={identity.Value}");
            }
        }

        return new LegacyMarkedTextProjection(header.Trim(), current, identities);
    }

    private static string RemoveProjectionBlocks(
        string content,
        ICollection<LogicalArtifactIdentity> parsedIdentities)
    {
        var output = new StringBuilder(content.Length);
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        var cursor = 0;
        while (cursor < content.Length)
        {
            var start = content.IndexOf(
                WorkerContextProjectionBoundary.StartPrefix,
                cursor,
                StringComparison.Ordinal);
            var unexpectedEnd = content.IndexOf(
                WorkerContextProjectionBoundary.EndPrefix,
                cursor,
                StringComparison.Ordinal);
            if (start < 0 && unexpectedEnd < 0)
            {
                output.Append(content, cursor, content.Length - cursor);
                break;
            }

            if (unexpectedEnd >= 0 && (start < 0 || unexpectedEnd < start))
            {
                throw new InvalidOperationException("Typed context brief contains a projection end boundary without a preceding start boundary.");
            }

            var (identity, startEnd) = ReadMarker(
                content,
                start,
                WorkerContextProjectionBoundary.StartPrefix,
                isStart: true);
            if (!seenIdentities.Add(identity.Value))
            {
                throw new InvalidOperationException($"Typed context brief contains duplicate projection boundaries for '{identity.Value}'.");
            }
            parsedIdentities.Add(identity);

            output.Append(content, cursor, start - cursor);
            var nestedStart = content.IndexOf(
                WorkerContextProjectionBoundary.StartPrefix,
                startEnd,
                StringComparison.Ordinal);
            var end = content.IndexOf(
                WorkerContextProjectionBoundary.EndPrefix,
                startEnd,
                StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException($"Typed context brief projection '{identity.Value}' has no closing boundary.");
            }

            if (nestedStart >= 0 && nestedStart < end)
            {
                throw new InvalidOperationException($"Typed context brief projection '{identity.Value}' contains a nested projection boundary.");
            }

            var (closingIdentity, endEnd) = ReadMarker(
                content,
                end,
                WorkerContextProjectionBoundary.EndPrefix,
                isStart: false);
            if (closingIdentity != identity)
            {
                throw new InvalidOperationException(
                    $"Typed context brief projection '{identity.Value}' closes with mismatched boundary '{closingIdentity.Value}'.");
            }

            cursor = endEnd;
        }

        return RestoreLiterals(output.ToString());
    }

    private static string RestoreLiterals(string content)
    {
        var output = new StringBuilder(content.Length);
        var cursor = 0;
        while (cursor < content.Length)
        {
            var start = content.IndexOf(
                WorkerContextProjectionBoundary.LiteralPrefix,
                cursor,
                StringComparison.Ordinal);
            if (start < 0)
            {
                output.Append(content, cursor, content.Length - cursor);
                break;
            }

            if (start > 0 && content[start - 1] != '\n')
            {
                throw new InvalidOperationException("Typed context projection literal markers must occupy a complete line.");
            }

            output.Append(content, cursor, start - cursor);
            var lineEnd = content.IndexOf('\n', start);
            var markerEnd = lineEnd < 0 ? content.Length : lineEnd;
            var markerTextEnd = markerEnd > start && content[markerEnd - 1] == '\r'
                ? markerEnd - 1
                : markerEnd;
            var marker = content[start..markerTextEnd];
            try
            {
                output.Append(WorkerContextProjectionBoundary.RestoreReservedLiteral(marker));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException("Typed context projection literal marker is malformed.", exception);
            }

            if (lineEnd >= 0)
            {
                output.Append(content, markerTextEnd, lineEnd - markerTextEnd + 1);
                cursor = lineEnd + 1;
            }
            else
            {
                cursor = content.Length;
            }
        }

        return output.ToString();
    }

    private static (LogicalArtifactIdentity Identity, int MarkerEnd) ReadMarker(
        string content,
        int start,
        string prefix,
        bool isStart)
    {
        if (start > 0 && content[start - 1] != '\n')
        {
            throw new InvalidOperationException("Typed context projection boundaries must occupy a complete line.");
        }

        var lineEnd = content.IndexOf('\n', start);
        var markerEnd = lineEnd < 0 ? content.Length : lineEnd;
        var markerTextEnd = markerEnd > start && content[markerEnd - 1] == '\r'
            ? markerEnd - 1
            : markerEnd;
        var marker = content[start..markerTextEnd];
        if (!marker.EndsWith(WorkerContextProjectionBoundary.MarkerSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Typed context projection boundary is malformed.");
        }

        var identityText = marker[
            prefix.Length..^WorkerContextProjectionBoundary.MarkerSuffix.Length];
        LogicalArtifactIdentity identity;
        try
        {
            identity = new LogicalArtifactIdentity(identityText);
            var expected = isStart
                ? WorkerContextProjectionBoundary.Start(identity)
                : WorkerContextProjectionBoundary.End(identity);
            if (!string.Equals(marker, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Typed context projection boundary is malformed.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Typed context projection boundary identity is invalid.", exception);
        }

        return (identity, markerEnd);
    }

    private static string RemoveMarkedBriefBlock(string content, string startMarker, string endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return content;
        }

        var end = content.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"Typed context brief block '{startMarker}' has no closing marker '{endMarker}'.");
        }

        end += endMarker.Length;
        while (end < content.Length && (content[end] == '\r' || content[end] == '\n'))
        {
            end++;
        }

        return content.Remove(start, end - start);
    }

    private static string RemoveLineRange(string content, string startPrefix, string endPrefix)
    {
        var start = FindLineWithPrefix(content, startPrefix, 0);
        if (start < 0)
        {
            return content;
        }

        var end = FindLineWithPrefix(content, endPrefix, start + startPrefix.Length);
        if (end < 0)
        {
            throw new InvalidOperationException($"Typed context brief source '{startPrefix}' has no boundary '{endPrefix}'.");
        }

        return content.Remove(start, end - start);
    }

    private static string RemoveLineWithPrefix(string content, string prefix)
    {
        var start = FindLineWithPrefix(content, prefix, 0);
        if (start < 0)
        {
            return content;
        }

        var end = content.IndexOf('\n', start);
        return end < 0 ? content[..start] : content.Remove(start, end - start + 1);
    }

    private static int FindLineWithPrefix(string content, string prefix, int startIndex) =>
        FindBriefHeading(content, prefix, startIndex);

    private static int FindBriefHeading(string content, string heading, int startIndex)
    {
        var candidate = content.IndexOf(heading, startIndex, StringComparison.Ordinal);
        while (candidate >= 0)
        {
            if (candidate == 0 || content[candidate - 1] == '\n')
            {
                return candidate;
            }

            candidate = content.IndexOf(heading, candidate + heading.Length, StringComparison.Ordinal);
        }

        return -1;
    }
}

internal sealed record LegacyMarkedTextProjection(
    string HeaderResidual,
    string CurrentBrief,
    IReadOnlyList<LogicalArtifactIdentity> ProjectedIdentities);
