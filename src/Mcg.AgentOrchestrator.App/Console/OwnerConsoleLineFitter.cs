using System.Text;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Fit in terminal cells, cutting title spans before the prefix or outcome. The model retains full text.
internal static class OwnerConsoleLineFitter
{
    internal const int MinimumTitleColumns = 16;

    internal static string Fit(string line, OwnerConsoleLineSpans spans, int width)
    {
        if (width <= 0 || line.GetColumns() <= width) return line;
        var outcomeStart = ValidOutcome(line, spans) ? spans.OutcomeStart : -1;
        if (!ValidTitles(line, spans, outcomeStart))
            return FitAroundOutcome(line, outcomeStart, spans.OutcomeLength, width);

        var titles = spans.Titles.Select(span => line.Substring(span.Start, span.Length)).ToArray();
        var lengths = titles.Select(title => title.GetColumns()).ToArray();
        var floors = spans.Titles.Select((span, i) => Math.Min(lengths[i], Math.Max(MinimumTitleColumns,
            span.PrefixLength == 0 ? 0 : (titles[i][..span.PrefixLength] + " …").GetColumns()))).ToArray();
        // A wide rune at the boundary can leave Cut one cell short; keep the readable floor.
        for (var i = 0; i < floors.Length; i++)
            if (Cut(titles[i], floors[i]).GetColumns() < floors[i]) floors[i]++;
        var budgets = lengths.ToArray();
        var overflow = line.GetColumns() - width;
        while (overflow > 0)
        {
            var active = Enumerable.Range(0, budgets.Length).Where(i => budgets[i] > floors[i])
                .OrderByDescending(i => lengths[i]).ThenBy(i => spans.Titles[i].Start).ToArray();
            if (active.Length == 0) break;
            var total = active.Sum(i => (long)lengths[i]);
            var shares = active.Select(i => (int)((long)overflow * lengths[i] / total)).ToArray();
            var remainder = overflow - shares.Sum();
            for (var j = 0; j < remainder; j++) shares[j]++;
            var removed = 0;
            for (var j = 0; j < active.Length; j++)
            {
                var i = active[j];
                var take = Math.Min(shares[j], budgets[i] - floors[i]);
                budgets[i] -= take;
                removed += take;
            }
            overflow -= removed;
        }

        var replacements = titles.Select((title, i) => Cut(title, budgets[i])).ToArray();
        var elided = new bool[titles.Length];
        var fitted = Render();
        for (var i = titles.Length - 1; i >= 0 && fitted.GetColumns() > width; i--)
        {
            if (spans.Titles[i].PrefixLength == 0) continue;
            var prefixOnly = titles[i][..spans.Titles[i].PrefixLength] + " …";
            if (prefixOnly.GetColumns() >= replacements[i].GetColumns()) continue;
            replacements[i] = prefixOnly;
            fitted = Render();
        }
        for (var i = titles.Length - 1; i >= 0 && fitted.GetColumns() > width; i--)
        {
            replacements[i] = "…";
            elided[i] = true;
            fitted = Render();
        }
        return FitAroundOutcome(fitted, outcomeStart, spans.OutcomeLength, width);

        string Render()
        {
            var result = new StringBuilder();
            var cursor = 0;
            var shift = 0;
            foreach (var i in Enumerable.Range(0, titles.Length))
            {
                var span = spans.Titles[i];
                var replacedLength = span.Length + (elided[i] ? span.TrailingLength : 0);
                result.Append(line, cursor, span.Start - cursor).Append(replacements[i]);
                cursor = span.Start + replacedLength;
                if (spans.OutcomeStart >= cursor) shift += replacements[i].Length - replacedLength;
            }
            result.Append(line, cursor, line.Length - cursor);
            if (outcomeStart >= 0) outcomeStart = spans.OutcomeStart + shift;
            return result.ToString();
        }
    }

    private static bool ValidOutcome(string line, OwnerConsoleLineSpans spans) =>
        spans.OutcomeStart >= 0 && spans.OutcomeLength > 0 &&
        (long)spans.OutcomeStart + spans.OutcomeLength <= line.Length;

    private static bool ValidTitles(string line, OwnerConsoleLineSpans spans, int outcomeStart)
    {
        var end = 0;
        foreach (var span in spans.Titles)
        {
            var next = (long)span.Start + span.Length + span.TrailingLength;
            if (span.Start < end || span.Length < 0 || span.TrailingLength < 0 ||
                span.PrefixLength < 0 || span.PrefixLength > span.Length || next > line.Length ||
                outcomeStart >= 0 && span.Start < outcomeStart + spans.OutcomeLength && next > outcomeStart)
                return false;
            end = (int)next;
        }
        return true;
    }

    private static string FitAroundOutcome(string line, int start, int length, int width)
    {
        if (line.GetColumns() <= width) return line;
        if (start < 0) return Cut(line, width);
        var outcome = line.Substring(start, length);
        if (outcome.GetColumns() > width) return Cut(outcome, width);
        var before = line[..start];
        var after = line[(start + length)..];
        var overflow = line.GetColumns() - width;
        after = Cut(after, Math.Max(0, after.GetColumns() - overflow));
        before = Cut(before, width - outcome.GetColumns() - after.GetColumns());
        return before + outcome + after;
    }

    internal static string Cut(string text, int width)
    {
        if (width <= 0) return "";
        if (text.GetColumns() <= width) return text;
        var result = new StringBuilder();
        var cells = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.GetColumns();
            if (cells + size > width - 1) break;
            result.Append(rune);
            cells += size;
        }
        return result + "…";
    }
}
