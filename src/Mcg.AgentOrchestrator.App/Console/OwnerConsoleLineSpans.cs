namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// UTF-16 offsets into the full narrated text; widths are measured separately in terminal cells.
internal sealed record OwnerConsoleTitleSpan(int Start, int Length, int PrefixLength, int TrailingLength = 0);

internal sealed record OwnerConsoleLineSpans(
    IReadOnlyList<OwnerConsoleTitleSpan> Titles, int OutcomeStart = -1, int OutcomeLength = 0)
{
    internal static OwnerConsoleLineSpans None { get; } = new([]);

    internal OwnerConsoleLineSpans Shift(int offset) => new(
        Titles.Select(span => span with { Start = span.Start + offset }).ToArray(),
        OutcomeStart < 0 ? -1 : OutcomeStart + offset, OutcomeLength);
}
