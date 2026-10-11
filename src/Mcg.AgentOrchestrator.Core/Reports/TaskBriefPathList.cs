namespace Mcg.AgentOrchestrator.Core;

/// <summary>
/// An ordered scope and the exact segment lines that present its preview. Line coordinates
/// refer to entries in TaskBriefSegment.Lines, including entries containing embedded newlines.
/// </summary>
public sealed record TaskBriefPathList
{
    public TaskBriefPathList(
        IReadOnlyList<string> paths,
        int total,
        int? previewBound,
        int firstListLineIndex,
        int listLineCount,
        int segmentLineCount,
        int? countLineIndex = null,
        string? countLabel = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        if (previewBound is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(previewBound));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(segmentLineCount);
        ArgumentOutOfRangeException.ThrowIfNegative(firstListLineIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(listLineCount);
        if (firstListLineIndex > segmentLineCount || listLineCount > segmentLineCount - firstListLineIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(listLineCount));
        }
        if (countLineIndex is { } index &&
            (index < 0 || index >= segmentLineCount ||
             (index >= firstListLineIndex && index - firstListLineIndex < listLineCount)))
        {
            throw new ArgumentOutOfRangeException(nameof(countLineIndex));
        }
        if (countLineIndex.HasValue != !string.IsNullOrWhiteSpace(countLabel))
        {
            throw new ArgumentException("A count line requires an emission label and vice versa.", nameof(countLabel));
        }

        Paths = Array.AsReadOnly(paths.ToArray());
        Total = total;
        PreviewBound = previewBound;
        FirstListLineIndex = firstListLineIndex;
        ListLineCount = listLineCount;
        CountLineIndex = countLineIndex;
        CountLabel = countLabel;
    }

    public IReadOnlyList<string> Paths { get; }
    public int Total { get; }
    public int? PreviewBound { get; }
    public int FirstListLineIndex { get; }
    public int ListLineCount { get; }
    public int? CountLineIndex { get; }
    public string? CountLabel { get; }
}
