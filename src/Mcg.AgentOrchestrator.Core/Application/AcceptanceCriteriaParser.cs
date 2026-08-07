namespace Mcg.AgentOrchestrator.Core;

public sealed record AcceptanceCriterion(
    string Name,
    string Type,
    string? Command = null,
    string? Pattern = null,
    string? Path = null);

public static class AcceptanceCriteriaParser
{
    private static readonly string[] KnownExecutables =
    [
        "dotnet", "git", "npm", "go", "cargo", "make",
        "python", "python3", "node", "npx", "yarn",
        "pwsh", "powershell", "bash", "sh", "curl", "wget"
    ];

    public static IReadOnlyList<AcceptanceCriterion> Parse(string objectiveText)
    {
        var results = new List<AcceptanceCriterion>();
        foreach (var criterionText in ParseDeclared(objectiveText))
        {
            var criterion = TryClassify(criterionText);
            if (criterion is not null)
                results.Add(criterion);
        }

        return results;
    }

    public static IReadOnlyList<string> ParseDeclared(string objectiveText)
    {
        var lines = objectiveText.ReplaceLineEndings("\n").Split('\n');
        var start = FindAcceptanceSection(lines);
        if (start < 0)
            return [];

        var results = new List<string>();
        string? current = null;
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsH2Heading(line))
                break;

            var trimmed = line.Trim();
            if (TryStripListPrefix(trimmed, out var criterionText))
            {
                AddCurrent();
                current = criterionText;
                continue;
            }

            if (current is not null &&
                !string.IsNullOrWhiteSpace(trimmed) &&
                char.IsWhiteSpace(line[0]))
            {
                current = $"{current} {trimmed}";
            }
        }

        AddCurrent();
        return results;

        void AddCurrent()
        {
            if (!string.IsNullOrWhiteSpace(current))
                results.Add(current);
            current = null;
        }
    }

    private static int FindAcceptanceSection(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsH2Heading(lines[i]))
                continue;
            var heading = lines[i].TrimStart().TrimStart('#').Trim();
            if (heading.StartsWith("Acceptance", StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }

        return -1;
    }

    private static bool IsH2Heading(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("##", StringComparison.Ordinal) &&
               !t.StartsWith("###", StringComparison.Ordinal);
    }

    private static bool TryStripListPrefix(string trimmedLine, out string criterionText)
    {
        criterionText = string.Empty;
        if (trimmedLine.StartsWith("- ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("* ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("• ", StringComparison.Ordinal))
        {
            criterionText = trimmedLine[2..].TrimStart();
            return criterionText.Length > 0;
        }

        var digitCount = 0;
        while (digitCount < trimmedLine.Length && char.IsDigit(trimmedLine[digitCount]))
            digitCount++;

        if (digitCount == 0 ||
            digitCount + 1 >= trimmedLine.Length ||
            trimmedLine[digitCount] is not ('.' or ')') ||
            !char.IsWhiteSpace(trimmedLine[digitCount + 1]))
        {
            return false;
        }

        criterionText = trimmedLine[(digitCount + 1)..].TrimStart();
        return criterionText.Length > 0;
    }

    private static AcceptanceCriterion? TryClassify(string bulletText)
    {
        var lower = bulletText.ToLowerInvariant();
        var tokens = ExtractBacktickTokens(bulletText);

        if (TryMatchGrepAbsent(lower, tokens, out var absentPattern))
            return new AcceptanceCriterion(bulletText, "grep-absent", Pattern: absentPattern);

        if (TryMatchGrepPresent(lower, tokens, out var presentPattern))
            return new AcceptanceCriterion(bulletText, "grep-present", Pattern: presentPattern);

        if (TryMatchFileExists(lower, tokens, out var filePath))
            return new AcceptanceCriterion(bulletText, "file-exists", Path: filePath);

        if (TryMatchCommandExit(bulletText, tokens, out var command))
            return new AcceptanceCriterion(bulletText, "command-exit", Command: command);

        return null;
    }

    private static bool TryMatchGrepAbsent(
        string lower,
        List<string> tokens,
        out string? pattern)
    {
        pattern = null;

        var hasNo = lower.Contains(" no ", StringComparison.Ordinal) ||
                    lower.StartsWith("no ", StringComparison.Ordinal);

        var hasAbsenceSignal =
            lower.Contains("grep", StringComparison.Ordinal) ||
            lower.Contains("remains", StringComparison.Ordinal) ||
            lower.Contains("absent", StringComparison.Ordinal) ||
            lower.Contains("removed", StringComparison.Ordinal);

        if (!hasNo || !hasAbsenceSignal)
            return false;

        return TryPickCodePattern(tokens, out pattern);
    }

    private static bool TryMatchGrepPresent(
        string lower,
        List<string> tokens,
        out string? pattern)
    {
        pattern = null;

        var hasNo = lower.Contains(" no ", StringComparison.Ordinal) ||
                    lower.StartsWith("no ", StringComparison.Ordinal);

        var hasGrepConfirms = lower.Contains("grep confirm", StringComparison.Ordinal);
        var hasStillRef = lower.Contains("still reference", StringComparison.Ordinal) ||
                          lower.Contains("still call", StringComparison.Ordinal);

        if ((!hasGrepConfirms && !hasStillRef) || hasNo)
            return false;

        return TryPickCodePattern(tokens, out pattern);
    }

    private static bool TryMatchFileExists(
        string lower,
        List<string> tokens,
        out string? filePath)
    {
        filePath = null;

        var hasFileSignal =
            lower.Contains("exists", StringComparison.Ordinal) ||
            lower.Contains("is produced", StringComparison.Ordinal) ||
            lower.Contains("is present", StringComparison.Ordinal) ||
            lower.Contains("creates ", StringComparison.Ordinal) ||
            lower.Contains("produced for", StringComparison.Ordinal);

        if (!hasFileSignal)
            return false;

        foreach (var token in tokens)
        {
            if (LooksLikeFilePath(token))
            {
                filePath = token;
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchCommandExit(
        string bulletText,
        List<string> tokens,
        out string? command)
    {
        command = null;

        if (tokens.Count == 0 || !bulletText.TrimStart().StartsWith("`", StringComparison.Ordinal))
            return false;

        var firstToken = tokens[0];
        var firstWord = firstToken.Split([' '], 2)[0];
        if (!IsKnownExecutable(firstWord))
            return false;

        command = firstToken;
        return true;
    }

    private static bool TryPickCodePattern(List<string> tokens, out string? pattern)
    {
        pattern = null;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (!IsKnownExecutable(token.Split([' '], 2)[0]) && !LooksLikeFilePath(token))
            {
                pattern = token;
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeFilePath(string token)
    {
        if (token.Contains(' ', StringComparison.Ordinal))
            return false;
        return (token.Contains('/', StringComparison.Ordinal) ||
                token.Contains('\\', StringComparison.Ordinal)) &&
               !IsKnownExecutable(token.Split([' '], 2)[0]);
    }

    private static bool IsKnownExecutable(string word) =>
        KnownExecutables.Any(exe => exe.Equals(word, StringComparison.OrdinalIgnoreCase));

    private static List<string> ExtractBacktickTokens(string text)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '`')
                continue;
            if (start < 0)
            {
                start = i + 1;
            }
            else
            {
                tokens.Add(text[start..i]);
                start = -1;
            }
        }

        return tokens;
    }
}
