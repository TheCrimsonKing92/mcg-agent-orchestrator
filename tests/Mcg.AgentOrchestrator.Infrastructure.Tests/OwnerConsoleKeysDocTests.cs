// Parallel-safe: verified repository document reads and in-memory negative controls.
public sealed class OwnerConsoleKeysDocTests
{
    [Fact]
    public void Document_KeysSection_StatesSevenRulesBeforeNonGoals() => Assert.Empty(Errors(ReadDocument()));

    [Fact]
    public void Checker_MissingRuleFive_ReportsTheMissingRule()
    {
        var text = string.Join("\n", ReadDocument().Replace("\r\n", "\n").Split('\n')
            .Where(line => !line.StartsWith("5. A key", StringComparison.Ordinal)));
        Assert.Contains("Missing rule 5: leaves the console", Errors(text));
    }

    [Fact]
    public void Checker_MissingHeading_ReportsTheMissingSection() =>
        Assert.Contains("Missing Keys and commands heading", Errors(ReadDocument().Replace("## Keys and commands", "Keys and commands")));

    private static string ReadDocument() => File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "docs", "console-display-plan.md"));

    private static IReadOnlyList<string> Errors(string document)
    {
        var lines = document.Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, "## Keys and commands");
        if (start < 0) return ["Missing Keys and commands heading"];
        var end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        var errors = new List<string>();
        if (end < 0 || lines[end] != "## Non-goals") errors.Add("Keys and commands must precede Non-goals");
        var section = lines.Skip(start + 1).Take((end < 0 ? lines.Length : end) - start - 1).ToArray();
        if (section.Any(line => line.Contains("```", StringComparison.Ordinal))) errors.Add("Keys section has a code fence");
        string[] keywords = ["typed command", "noun", "earned", "argument", "leaves the console", "reserved", "fails silently"];
        var prior = -1;
        for (var index = 0; index < keywords.Length; index++)
        {
            var number = index + 1;
            var position = Array.FindIndex(section, line => line.StartsWith($"{number}. ", StringComparison.Ordinal) &&
                line.Contains(keywords[index], StringComparison.Ordinal));
            if (position < 0) errors.Add($"Missing rule {number}: {keywords[index]}");
            else if (position <= prior) errors.Add("Rules are out of order");
            prior = position;
        }
        if (!section.Any(line => line.Contains("OwnerConsoleBindings", StringComparison.Ordinal) &&
            line.Contains("OwnerConsoleBindingsTests", StringComparison.Ordinal))) errors.Add("Missing registry and tests pointer");
        return errors;
    }
}
