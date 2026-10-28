public sealed class EnvironmentVariableCensusTests
{
    // Parallel-safe: synthetic controls use only local strings and no filesystem or process state.
    [Xunit.Fact]
    public void FindUnlisted_MissingName_ReportsNameDespiteOtherMentions()
    {
        const string source = """const string Variable = "MCG_SYNTHETIC_NAME";""";
        const string table = """
            MCG_SYNTHETIC_NAME is mentioned here, but has no census row.
            | Name | Reading path:line | Category | Default | Setter | Target home |
            | --- | --- | --- | --- | --- | --- |
            | `MCG_OTHER_NAME` | synthetic.cs:1 | setting | MCG_SYNTHETIC_NAME | tests | config |
            """;

        var missing = EnvironmentVariableCensusScanner.FindUnlisted([source], table);

        Xunit.Assert.Equal(new[] { "MCG_SYNTHETIC_NAME" }, missing);
    }

    [Xunit.Fact]
    public void FindUnlisted_ListedName_ReportsNothing()
    {
        const string source = """const string Variable = "MCG_SYNTHETIC_NAME";""";
        const string table = """
            | Name | Reading path:line | Category | Default | Setter | Target home |
            | --- | --- | --- | --- | --- | --- |
            | `MCG_SYNTHETIC_NAME` | synthetic.cs:1 | setting | absent | tests | config |
            """;

        var missing = EnvironmentVariableCensusScanner.FindUnlisted([source], table);

        Xunit.Assert.Empty(missing);
    }

    [Xunit.Fact]
    public void FindUnlisted_RepeatedMissingNames_ReturnsDistinctOrdinalNames()
    {
        string[] sources =
        [
            """const string Z = "MCG_Z_MISSING"; const string A = "MCG_A_MISSING";""",
            """const string Duplicate = "MCG_Z_MISSING";"""
        ];

        var missing = EnvironmentVariableCensusScanner.FindUnlisted(sources, string.Empty);

        Xunit.Assert.Equal(new[] { "MCG_A_MISSING", "MCG_Z_MISSING" }, missing);
    }

    // Parallel-safe: reads the explicit candidate root without modifying files or process state.
    [Xunit.Fact]
    public void RepositorySources_AllNames_HaveCompleteUniqueCensusRows()
    {
        var root = VerifiedRepositoryRoot.Find();
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        Xunit.Assert.NotEmpty(files);
        var sources = files.Select(File.ReadAllText).ToArray();
        var names = EnvironmentVariableCensusScanner.DiscoverNames(sources);
        Xunit.Assert.Contains("MCG_WORKER_ACCOUNT", names);

        var table = File.ReadAllText(Path.Combine(root, "docs", "environment-variables.md"));
        var missing = EnvironmentVariableCensusScanner.FindUnlisted(sources, table);
        Xunit.Assert.True(missing.Count == 0, "Unlisted source names: " + string.Join(", ", missing));

        var rows = EnvironmentVariableCensusScanner.ReadRows(table);
        Xunit.Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Xunit.Assert.True(row.Length == 6, $"{row[0]} must have six cells; found {row.Length}.");
            Xunit.Assert.All(row, cell => Xunit.Assert.False(string.IsNullOrWhiteSpace(cell)));
        }

        var listed = rows.Select(row => row[0]).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        // Sequence equality rejects duplicates and extra rows as well as omissions.
        Xunit.Assert.Equal(names.ToArray(), listed);
    }

    [Xunit.Fact]
    public void SandboxConstant_Discovery_IncludesNameWithoutLiteralAtReadCall()
    {
        var root = VerifiedRepositoryRoot.Find();
        var source = File.ReadAllText(Path.Combine(root,
            "src", "Mcg.AgentOrchestrator.Execution", "Processes", "WorkerSandboxOptions.cs"));

        Xunit.Assert.Contains("GetEnvironmentVariable(AccountVariable)", source);
        Xunit.Assert.Contains("MCG_WORKER_ACCOUNT",
            EnvironmentVariableCensusScanner.DiscoverNames([source]));
    }
}
