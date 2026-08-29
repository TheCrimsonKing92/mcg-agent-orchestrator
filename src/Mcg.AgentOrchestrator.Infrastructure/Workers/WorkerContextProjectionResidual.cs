using Mcg.AgentOrchestrator.Core;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerContextProjectionResidual
{
    internal static string RemoveProjectionBlocks(string content)
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

    internal static string RestoreLiterals(string content)
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
}
