using Mcg.AgentOrchestrator.App.Cli;

public sealed class CliHelpTestsIntentMetadata
{
    [Xunit.Theory]
    [Xunit.InlineData("progress", false)]
    [Xunit.InlineData("progress", true)]
    [Xunit.InlineData("retry", false)]
    [Xunit.InlineData("retry", true)]
    [Xunit.InlineData("verify-manual", false)]
    [Xunit.InlineData("verify-manual", true)]
    public void IndependentMetadataFlagsRemainSeparateFromEvidence(string command, bool interactive)
    {
        foreach (var metadata in new string[][] {
            ["--idempotency-key", "fixture-key"], ["--operator-actor", "Fixture Operator"],
            ["--operator-actor", "Fixture Operator", "--idempotency-key", "fixture-key"],
            ["--idempotency-key=fixture-key"] })
        {
            string[] positionals = command == "retry" ? ["1"] : ["1", command == "progress" ? "failed" : "passed"];
            var normalized = Normalize([command, "--goal", "goal-prefix", .. positionals, "preserve existing evidence", .. metadata], interactive);
            Xunit.Assert.Contains("preserve existing evidence", normalized);
            var keyIndex = Array.IndexOf(normalized, "--idempotency-key");
            if (metadata.Any(item => item.StartsWith("--idempotency-key", StringComparison.Ordinal)))
                Xunit.Assert.Equal("fixture-key", normalized[keyIndex + 1]);
            else Xunit.Assert.Equal(-1, keyIndex);
            var actorIndex = Array.IndexOf(normalized, "--operator-actor");
            if (metadata.Contains("--operator-actor")) Xunit.Assert.Equal("Fixture Operator", normalized[actorIndex + 1]);
            else Xunit.Assert.Equal(-1, actorIndex);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void QuotedEvidenceDoesNotManufactureMetadata(bool interactive)
    {
        const string evidence = "Explain the literal --idempotency-key example without applying it";
        var normalized = Normalize(["verify-manual", "1", "passed", evidence], interactive);
        Xunit.Assert.Equal(new[] { "verify-manual", "1", "passed", evidence }, normalized);
    }

    [Xunit.Theory]
    [Xunit.InlineData("--idempotency-key", false)]
    [Xunit.InlineData("--idempotency-key", true)]
    [Xunit.InlineData("--operator-actor", false)]
    [Xunit.InlineData("--operator-actor", true)]
    public void MissingAndDuplicateMetadataValuesAreRejected(string flag, bool interactive)
    {
        Xunit.Assert.Throws<ArgumentException>(() => Normalize(["retry", "1", "keep evidence", flag], interactive));
        Xunit.Assert.Throws<ArgumentException>(() => Normalize(["retry", "1", "keep evidence", flag, "first", flag, "second"], interactive));
    }

    private static string[] Normalize(string[] args, bool interactive) =>
        (interactive
            ? CliArgumentParser.SplitCommand(args[0] + " " + string.Join(' ', args.Skip(1).Select(part => "\"" + part.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"")))
            : CliArgumentParser.NormalizeArgs(args)).ToArray();
}
