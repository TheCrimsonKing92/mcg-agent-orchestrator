using System.Text.RegularExpressions;

// Parallel-safe: reads immutable repository files; controls use in-memory document mutations.
public sealed class ConsoleDisplayPlanDocumentTests
{
    private const string DocumentPath = "docs/console-display-plan.md";
    private const string ConsolePath = "src/Mcg.AgentOrchestrator.App/Console";
    private static readonly string[] InventoryNames =
    [
        "OwnerConsoleFullScreenView", "OwnerConsoleEpicDialog", "OwnerConsoleGateDialog",
        "OwnerConsoleDecisionDialog", "OwnerConsoleExperimentDecisionDialog",
        "OwnerConsoleTextDialog", "OwnerConsoleGoalDialog", "OwnerConsoleTextPage",
        "OwnerConsoleNoticeStrip", "OwnerConsoleBoardRenderer"
    ];
    private static readonly string[] Headings =
    [
        "## Inventory", "## Use-case classification", "## Recommended display form per use case",
        "## Shared visual vocabulary", "## Prioritized slice list", "## Non-goals"
    ];

    [Xunit.Fact]
    public void Inventory_BaselineTypes_AllExistAndAreNamed()
    {
        var root = VerifiedRepositoryRoot.Find();
        var document = ReadDocument(root);
        var declarations = Directory.GetFiles(Path.Combine(root, ConsolePath), "*.cs")
            .SelectMany(path => ViewNames(File.ReadAllText(path))).ToHashSet(StringComparer.Ordinal);
        var absentTypes = InventoryNames.Where(name => !declarations.Contains(name)).ToArray();
        Xunit.Assert.True(absentTypes.Length == 0, "Missing console declarations: " + string.Join(", ", absentTypes));

        var missing = MissingNames(document);
        Xunit.Assert.True(missing.Length == 0, "Missing inventory names: " + string.Join(", ", missing));

        const string removed = "OwnerConsoleTextPage";
        var incomplete = Regex.Replace(document, @"\b" + Regex.Escape(removed) + @"\b", "");
        Xunit.Assert.Equal([removed], MissingNames(incomplete));
        // Discovery stays open to new views, while only the fixed baseline names bind the plan.
        Xunit.Assert.Equal(["FutureDialog"], ViewNames("internal class FutureDialog : Dialog { }"));
    }

    [Xunit.Fact]
    public void Mockups_AllowedCharactersAndTerminology_RejectInvalidText()
    {
        var document = ReadDocument(VerifiedRepositoryRoot.Find());
        var scan = ScanFences(document);
        Xunit.Assert.Null(scan.Error);
        Xunit.Assert.True(scan.Blocks >= 5, $"Expected at least 5 mockups, found {scan.Blocks}.");
        Xunit.Assert.False(ContainsRemovedTerm(document), "Document contains removed terminology.");

        var invalid = ScanFences("```text\n\u2192\n```\n");
        Xunit.Assert.Equal("U+2192 at line 2", invalid.Error);
        Xunit.Assert.Equal("Unclosed fence at line 1", ScanFences("```text\nvalid\n").Error);
        Xunit.Assert.True(ContainsRemovedTerm(("dash" + "board").ToUpperInvariant()));
    }

    [Xunit.Fact]
    public void Structure_OrderedSectionsAndSlices_HaveValidFileReferences()
    {
        var root = VerifiedRepositoryRoot.Find();
        var document = ReadDocument(root);
        Xunit.Assert.Empty(HeadingErrors(document));
        var slices = SliceBodies(document);
        Xunit.Assert.True(slices.Length >= 5, $"Expected at least 5 slices, found {slices.Length}.");
        var invalid = SliceErrors(document, root);
        Xunit.Assert.True(invalid.Length == 0, string.Join(Environment.NewLine, invalid));

        var swapped = document.Replace(Headings[0], "## Temporary", StringComparison.Ordinal)
            .Replace(Headings[1], Headings[0], StringComparison.Ordinal)
            .Replace("## Temporary", Headings[1], StringComparison.Ordinal);
        Xunit.Assert.NotEmpty(HeadingErrors(swapped));

        var source = ConsolePath + "/OwnerConsoleModeSelector.cs";
        var pastEnd = File.ReadAllLines(Path.Combine(root, source)).Length + 1;
        var badCitation = "## Prioritized slice list\n### Slice 1: Invalid citation\n" +
            $"Files: `{source}`\nData source: `{source}:{pastEnd}`\n" +
            "Plain mode: unchanged\n## Non-goals\n";
        Xunit.Assert.Equal(["Slice 1: invalid data source"], SliceErrors(badCitation, root));
        Xunit.Assert.Equal(["Slice 1: missing Files path"],
            SliceErrors(badCitation.Replace($"Files: `{source}`", "Files:", StringComparison.Ordinal)
                .Replace($":{pastEnd}`", ":1`", StringComparison.Ordinal), root));
        Xunit.Assert.Equal(["Slice 1: invalid data source"],
            SliceErrors(badCitation.Replace($"{source}:{pastEnd}", "../outside.cs:1", StringComparison.Ordinal), root));
        Xunit.Assert.Equal(["No slice entries"], SliceErrors("## Prioritized slice list\n## Non-goals\n", root));
    }

    private static string ReadDocument(string root)
    {
        var path = Path.Combine(root, DocumentPath);
        Xunit.Assert.True(File.Exists(path), "Missing plan document: " + DocumentPath);
        return File.ReadAllText(path);
    }

    private static string[] ViewNames(string source) =>
        Regex.Matches(source,
                @"\b(?:class|record(?:\s+(?:class|struct))?|struct)\s+(?<name>\w+)(?:\s*\([^;{}]*\))?(?:\s*:\s*(?<base>[\w.]+))?")
            .Where(match => Regex.IsMatch(match.Groups["name"].Value,
                               @"(?:Dialog|FullScreenView|TextPage|NoticeStrip|BoardRenderer)$") ||
                           Regex.IsMatch(match.Groups["base"].Value, @"(?:^|\.)(?:FrameView|Dialog|Window)$"))
            .Select(match => match.Groups["name"].Value).ToArray();

    private static string[] MissingNames(string text) => InventoryNames
        .Where(name => !Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b")).ToArray();

    private static bool ContainsRemovedTerm(string text) =>
        text.Contains("dash" + "board", StringComparison.OrdinalIgnoreCase);

    private static (string[] Outside, int Blocks, string? Error) ScanFences(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var outside = new string[lines.Length];
        var blocks = 0;
        var fence = "";
        var openingLine = 0;
        string? error = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            outside[index] = "";
            var marker = Regex.Match(line, @"^\s*(?<fence>`{3,}|~{3,})(?<tail>.*)$");
            if (fence.Length == 0)
            {
                if (marker.Success)
                {
                    fence = marker.Groups["fence"].Value;
                    openingLine = index + 1;
                    blocks++;
                }
                else outside[index] = line;
                continue;
            }

            if (marker.Success && marker.Groups["fence"].Value[0] == fence[0] &&
                marker.Groups["fence"].Length >= fence.Length &&
                string.IsNullOrWhiteSpace(marker.Groups["tail"].Value))
            {
                fence = "";
                continue;
            }

            foreach (var character in line)
                if (!AllowedCharacter(character))
                {
                    error ??= $"U+{(int)character:X4} at line {index + 1}";
                    break;
                }
        }
        if (fence.Length > 0) error ??= $"Unclosed fence at line {openingLine}";
        return (outside, blocks, error);
    }

    private static bool AllowedCharacter(char value) => value is >= '\u0020' and <= '\u007e' or
        '\t' or '\r' or '\n' or >= '\u2500' and <= '\u257f' or >= '\u2580' and <= '\u259f' or
        '\u00b7' or '\u2026' or '\u2014';

    private static string[] HeadingErrors(string text)
    {
        var actual = ScanFences(text).Outside.Where(line => line.StartsWith("## ", StringComparison.Ordinal)).ToArray();
        return actual.SequenceEqual(Headings, StringComparer.Ordinal)
            ? [] : ["Expected six ordered sections; found: " + string.Join(", ", actual)];
    }

    private static (int Number, string Body)[] SliceBodies(string text)
    {
        var outside = string.Join("\n", ScanFences(text).Outside);
        var section = Regex.Match(outside, @"(?m)^## Prioritized slice list\r?$\n(?<body>[\s\S]*?)(?=^## |\z)");
        return Regex.Matches(section.Groups["body"].Value,
                @"(?m)^### Slice (?<number>\d+):[^\n]*\n(?<body>[\s\S]*?)(?=^### |\z)")
            .Select(match => (int.TryParse(match.Groups["number"].Value, out var number) ? number : -1,
                match.Groups["body"].Value)).ToArray();
    }

    private static string[] SliceErrors(string text, string root)
    {
        var slices = SliceBodies(text);
        if (slices.Length == 0) return ["No slice entries"];
        var errors = new List<string>();
        for (var index = 0; index < slices.Length; index++)
        {
            var (number, body) = slices[index];
            var label = $"Slice {number}";
            if (number != index + 1) errors.Add(label + ": priority numbers must be consecutive");
            var files = Regex.Match(body, @"(?m)^Files:[^\n]*$").Value;
            if (!Regex.IsMatch(files, @"`[^`\r\n]*/[^`\r\n]+\.[A-Za-z0-9]+`"))
                errors.Add(label + ": missing Files path");
            var data = Regex.Match(body, @"(?m)^Data source:[^\n]*$").Value;
            var token = Regex.Match(data, @"`(?<reference>[^`]+)`");
            if (!token.Success || !ValidCitation(token.Groups["reference"].Value, root))
                errors.Add(label + ": invalid data source");
            if (!Regex.IsMatch(body, @"(?m)^Plain mode:\s*\S[^\n]*$"))
                errors.Add(label + ": missing plain-mode statement");
        }
        return errors.ToArray();
    }

    private static bool ValidCitation(string reference, string root)
    {
        var citation = Regex.Match(reference, @"^(?<path>[^:\r\n]+):(?<first>\d+)(?:-(?<last>\d+))?$");
        if (!citation.Success) return false;
        var relative = citation.Groups["path"].Value;
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Contains(".."))
            return false;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return false;
        var count = File.ReadAllLines(fullPath).Length;
        if (!int.TryParse(citation.Groups["first"].Value, out var first) || first < 1 || first > count) return false;
        return !citation.Groups["last"].Success ||
            (int.TryParse(citation.Groups["last"].Value, out var last) && last >= first && last <= count);
    }
}
