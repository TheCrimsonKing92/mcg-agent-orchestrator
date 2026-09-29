internal static class CSharpSourceLaunchPatternScanner
{
    private enum Kind { Code, Interpolation, LineComment, BlockComment, Character, Regular, Verbatim, Raw }

    private sealed class Frame(Kind kind, int quoteCount = 0, int dollarCount = 0)
    {
        public Kind Kind { get; } = kind;
        public int QuoteCount { get; } = quoteCount;
        public int DollarCount { get; } = dollarCount;
        public int BraceDepth { get; set; }
    }

    internal static bool[][] ClassifyCode(IReadOnlyList<string> lines)
    {
        var masks = new bool[lines.Count][];
        var frames = new Stack<Frame>();
        frames.Push(new Frame(Kind.Code));

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var mask = new bool[line.Length];
            masks[lineIndex] = mask;
            for (var i = 0; i < line.Length;)
            {
                var frame = frames.Peek();
                var current = line[i];
                if (frame.Kind is Kind.Code or Kind.Interpolation)
                {
                    if (frame.Kind == Kind.Interpolation && current == '}' && frame.BraceDepth == 0)
                    {
                        var width = frame.DollarCount;
                        if (HasRun(line, i, '}', width))
                        {
                            Mark(mask, i, width, true);
                            frames.Pop();
                            i += width;
                            continue;
                        }
                    }

                    if (current == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        frames.Push(new Frame(Kind.LineComment));
                        continue;
                    }
                    if (current == '/' && i + 1 < line.Length && line[i + 1] == '*')
                    {
                        frames.Push(new Frame(Kind.BlockComment));
                        i += 2;
                        continue;
                    }
                    if (current == '\'')
                    {
                        frames.Push(new Frame(Kind.Character));
                        mask[i++] = false;
                        continue;
                    }
                    if (TryOpenString(line, i, out var stringFrame, out var widthOpened))
                    {
                        Mark(mask, i, widthOpened, false);
                        frames.Push(stringFrame);
                        i += widthOpened;
                        continue;
                    }
                    mask[i] = true;
                    if (frame.Kind == Kind.Interpolation)
                    {
                        if (current == '{') frame.BraceDepth++;
                        if (current == '}' && frame.BraceDepth > 0) frame.BraceDepth--;
                    }
                    i++;
                    continue;
                }

                if (frame.Kind == Kind.LineComment)
                {
                    i = line.Length;
                    continue;
                }
                if (frame.Kind == Kind.BlockComment)
                {
                    if (current == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        frames.Pop();
                        i += 2;
                    }
                    else i++;
                    continue;
                }
                if (frame.Kind == Kind.Character || frame.Kind == Kind.Regular)
                {
                    if (current == '\\' && i + 1 < line.Length)
                    {
                        i += 2;
                        continue;
                    }
                    if ((frame.Kind == Kind.Character && current == '\'') ||
                        (frame.Kind == Kind.Regular && current == '"'))
                    {
                        frames.Pop();
                        i++;
                        continue;
                    }
                }
                if (frame.Kind == Kind.Verbatim && current == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') i += 2;
                    else { frames.Pop(); i++; }
                    continue;
                }
                if (frame.Kind == Kind.Raw && current == '"' && HasRun(line, i, '"', frame.QuoteCount))
                {
                    frames.Pop();
                    i += frame.QuoteCount;
                    continue;
                }
                if (frame.DollarCount > 0 && current == '{')
                {
                    if (frame.Kind != Kind.Raw && HasRun(line, i, '{', 2))
                    {
                        i += 2;
                        continue;
                    }
                    if (HasRun(line, i, '{', frame.DollarCount))
                    {
                        frames.Push(new Frame(Kind.Interpolation, dollarCount: frame.DollarCount));
                        Mark(mask, i, frame.DollarCount, true);
                        i += frame.DollarCount;
                        continue;
                    }
                }
                if (frame.DollarCount > 0 && frame.Kind != Kind.Raw && current == '}' &&
                    HasRun(line, i, '}', 2))
                {
                    i += 2;
                    continue;
                }
                i++;
            }

            if (frames.Peek().Kind == Kind.LineComment) frames.Pop();
            if (frames.Peek().Kind is Kind.Regular or Kind.Character) frames.Pop();
        }

        if (frames.Count != 1)
            throw new InvalidOperationException("C# source ended inside a comment, string, or interpolation.");

        return masks;
    }

    internal static bool ContainsCSharpLaunchInCode(string line, bool[] isCode)
    {
        return HasCodeMatch(line, isCode, "new " + "ProcessStartInfo") ||
               HasCodeMatch(line, isCode, "UseShellExecute" + " = false");
    }

    private static bool HasCodeMatch(string line, bool[] isCode, string pattern)
    {
        for (var start = 0; start <= line.Length - pattern.Length; start++)
        {
            var found = line.IndexOf(pattern, start, StringComparison.Ordinal);
            if (found < 0) return false;
            for (var index = found; index < found + pattern.Length; index++)
                if (isCode[index]) return true;
            start = found;
        }
        return false;
    }

    private static bool TryOpenString(string line, int start, out Frame frame, out int width)
    {
        var cursor = start;
        var dollars = 0;
        var verbatim = false;
        while (cursor < line.Length && line[cursor] == '$') { dollars++; cursor++; }
        if (cursor < line.Length && line[cursor] == '@') { verbatim = true; cursor++; }
        if (dollars == 0 && verbatim && cursor < line.Length && line[cursor] == '$')
        {
            dollars = 1;
            cursor++;
        }
        if (cursor < line.Length && line[cursor] == '"')
        {
            var quotes = CountRun(line, cursor, '"');
            if (quotes >= 3 && !verbatim)
            {
                frame = new Frame(Kind.Raw, quotes, dollars);
                width = cursor - start + quotes;
                return true;
            }
            if (quotes >= 1 && (cursor == start || dollars > 0 || verbatim))
            {
                frame = new Frame(verbatim ? Kind.Verbatim : Kind.Regular, dollarCount: dollars);
                width = cursor - start + 1;
                return true;
            }
        }
        frame = null!;
        width = 0;
        return false;
    }

    private static int CountRun(string line, int start, char character)
    {
        var count = 0;
        while (start + count < line.Length && line[start + count] == character) count++;
        return count;
    }

    private static bool HasRun(string line, int start, char character, int count) =>
        count > 0 && CountRun(line, start, character) >= count;

    private static void Mark(bool[] mask, int start, int count, bool value)
    {
        for (var i = start; i < start + count; i++) mask[i] = value;
    }
}
