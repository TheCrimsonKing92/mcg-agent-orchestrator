using System.Diagnostics;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: reads repository files and mutates only local start-info dictionaries.
public sealed class WorkerMcgEnvironmentAllowlistTests
{
    [Fact]
    public void Names_AreExactlyTheFourOrchestratorHandoffConstants()
    {
        string[] expected =
        [
            DispatchProcessHost.StartGatePathVariable,
            WorkerSandboxOptions.DispatchWorkerVariable,
            DotnetBuildStorageLayout.IsolatedRootOverrideVariable,
            "MCG_ORCHESTRATOR_DOTNET_PATH"
        ];

        Assert.Equal(4, WorkerMcgEnvironmentAllowlist.Names.Count);
        Assert.True(WorkerMcgEnvironmentAllowlist.Names.SetEquals(expected));
    }

    [Fact]
    public void DispatchProcessHost_ScrubsWorkerStartInfoBeforeMarkerAssignment()
    {
        var root = VerifiedRepositoryRoot.Find();
        var processDirectory = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Execution", "Processes");
        var lines = File.ReadAllLines(Path.Combine(processDirectory, "DispatchProcessHost.cs"))
            .Select(line => line.Trim()).ToArray();
        var scrubIndex = Array.IndexOf(lines,
            "WorkerMcgEnvironmentAllowlist.RemoveDisallowed(startInfo.Environment);");
        var markerIndex = Array.IndexOf(lines,
            "startInfo.Environment[WorkerSandboxOptions.DispatchWorkerVariable] = \"1\";");

        Assert.True(scrubIndex >= 0, "Worker launch must scrub its start-info environment.");
        Assert.Equal(scrubIndex + 1, markerIndex);

        var allowlistSource = File.ReadAllText(
            Path.Combine(processDirectory, "WorkerMcgEnvironmentAllowlist.cs"));
        Assert.DoesNotMatch(new Regex(@"\bpartial\b"), allowlistSource);
    }

    [Fact]
    public void RemoveDisallowed_SettingsAndIdentity_RemovesOnlyDisallowedNames()
    {
        var startInfo = new ProcessStartInfo { CreateNoWindow = true };
        startInfo.Environment.Clear();
        var expected = new Dictionary<string, string?>
        {
            [DispatchProcessHost.StartGatePathVariable] = "start-gate-path",
            [WorkerSandboxOptions.DispatchWorkerVariable] = "worker-marker",
            [DotnetBuildStorageLayout.IsolatedRootOverrideVariable] = "isolated-root",
            [WorkerMcgEnvironmentAllowlist.OrchestratorDotnetPathVariable] = "dotnet-path",
            ["PATH"] = "tool-path",
            ["mcg_lowercase"] = "nonmatching-prefix"
        };
        foreach (var (name, value) in expected)
            startInfo.Environment[name] = value;
        startInfo.Environment["MCG_ACCEPTANCE_CHECK_TIMEOUT_MINUTES"] = "17";
        startInfo.Environment["MCG_ORCHESTRATOR_TENANT"] = "host-tenant";

        WorkerMcgEnvironmentAllowlist.RemoveDisallowed(startInfo.Environment);

        Assert.False(startInfo.Environment.ContainsKey("MCG_ACCEPTANCE_CHECK_TIMEOUT_MINUTES"));
        Assert.False(startInfo.Environment.ContainsKey("MCG_ORCHESTRATOR_TENANT"));
        Assert.Equal(expected.Count, startInfo.Environment.Count);
        foreach (var (name, value) in expected)
            Assert.Equal(value, startInfo.Environment[name]);
    }

    [Fact]
    public void RemoveDisallowed_CaseInsensitiveDictionary_UsesOrdinalMembership()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MCG_ORCHESTRATOR_dotnet_path"] = "wrong-case-name",
            ["mcg_lowercase"] = "nonmatching-prefix"
        };

        WorkerMcgEnvironmentAllowlist.RemoveDisallowed(environment);

        var remaining = Assert.Single(environment);
        Assert.Equal("mcg_lowercase", remaining.Key);
        Assert.Equal("nonmatching-prefix", remaining.Value);
        Assert.DoesNotContain(WorkerMcgEnvironmentAllowlist.Names,
            name => environment.ContainsKey(name));
    }

    [Fact]
    public void Census_AllowlistedNames_AllDeclareOrchestratorHandoffs()
    {
        var census = File.ReadAllText(Path.Combine(
            VerifiedRepositoryRoot.Find(), "docs", "environment-variables.md"));

        Assert.Empty(FindCensusHandoffErrors(census));
    }

    [Fact]
    public void CensusGuard_OperatorOnlyRow_ReportsTheAllowlistedName()
    {
        var name = DotnetBuildStorageLayout.IsolatedRootOverrideVariable;
        var census = CreateCensus(WorkerMcgEnvironmentAllowlist.Names.Select(candidate =>
            $"| `{candidate}` | {(candidate == name ? "operator only" : "orchestrator for its own worker child")} |"));

        var error = Assert.Single(FindCensusHandoffErrors(census));

        Assert.Contains(name, error);
        Assert.Contains("not an orchestrator handoff", error);
    }

    [Theory]
    [InlineData("missing row")]
    [InlineData("missing cell")]
    [InlineData("conflicting duplicate")]
    public void CensusGuard_IncompleteOrConflictingRow_ReportsTheDefect(string defect)
    {
        var name = DispatchProcessHost.StartGatePathVariable;
        var rows = WorkerMcgEnvironmentAllowlist.Names
            .Where(candidate => candidate != name)
            .Select(candidate => $"| `{candidate}` | orchestrator for its own worker child |")
            .ToList();
        if (defect == "missing cell")
            rows.Add($"| `{name}` |");
        else if (defect == "conflicting duplicate")
        {
            rows.Add($"| `{name}` | orchestrator for its own dispatch host |");
            rows.Add($"| `{name}` | operator only |");
        }

        var error = Assert.Single(FindCensusHandoffErrors(CreateCensus(rows)));

        Assert.Contains(name, error);
        Assert.Contains(defect, error);
    }

    [Fact]
    public void CensusGuard_ReorderedFormattedColumns_AcceptsEquivalentHandoffs()
    {
        var descriptions = new[]
        {
            "**ORCHESTRATOR**   for its own DISPATCH HOST",
            "orchestrator sets it for its own worker child",
            "Passed by the orchestrator to its own worker child",
            "operator; orchestrator passes it to its own worker child"
        };
        var rows = WorkerMcgEnvironmentAllowlist.Names.Select((name, index) =>
            $"| {descriptions[index]} | `{name}` | unused |");
        var census = "| **Who sets it** | Name | Notes |\n| --- | --- | --- |\n"
            + string.Join("\n", rows);

        Assert.Empty(FindCensusHandoffErrors(census));
    }

    private static string CreateCensus(IEnumerable<string> rows) =>
        "| Name | Who sets it |\n| --- | --- |\n" + string.Join("\n", rows);

    private static IReadOnlyList<string> FindCensusHandoffErrors(string census)
    {
        var entries = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        var nameIndex = -1;
        var setterIndex = -1;
        foreach (var line in census.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('|'))
            {
                nameIndex = setterIndex = -1;
                continue;
            }

            var cells = Regex.Split(trimmed.Trim('|'), @"(?<!\\)\|")
                .Select(StripMarkdown).ToArray();
            var headerNameIndex = Array.FindIndex(cells,
                cell => cell.Equals("Name", StringComparison.OrdinalIgnoreCase));
            var headerSetterIndex = Array.FindIndex(cells,
                cell => cell.Equals("Who sets it", StringComparison.OrdinalIgnoreCase));
            if (headerNameIndex >= 0 && headerSetterIndex >= 0)
            {
                nameIndex = headerNameIndex;
                setterIndex = headerSetterIndex;
                continue;
            }

            if (nameIndex < 0 || nameIndex >= cells.Length
                || !WorkerMcgEnvironmentAllowlist.Names.Contains(cells[nameIndex]))
                continue;

            if (!entries.TryGetValue(cells[nameIndex], out var setters))
                entries[cells[nameIndex]] = setters = [];
            setters.Add(setterIndex < cells.Length ? cells[setterIndex] : null);
        }

        var errors = new List<string>();
        foreach (var name in WorkerMcgEnvironmentAllowlist.Names)
        {
            if (!entries.TryGetValue(name, out var setters))
                errors.Add($"{name}: missing row");
            else if (setters.Any(string.IsNullOrWhiteSpace))
                errors.Add($"{name}: missing cell for who sets it");
            else if (setters.Select(value => value!.ToLowerInvariant()).Distinct().Count() > 1)
                errors.Add($"{name}: conflicting duplicate rows");
            else if (!Regex.IsMatch(setters[0]!,
                @"\borchestrator\s+(?:(?:sets?|passes|passed)\b[^;.!]*?\s+)?(?:for|to)\s+its\s+own\s+(?:dispatch\s+host|worker\s+child)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                errors.Add($"{name}: who sets it is not an orchestrator handoff");
        }

        return errors;
    }

    private static string StripMarkdown(string cell) =>
        Regex.Replace(Regex.Replace(cell, @"[`*]|(?<!\w)_|_(?!\w)", ""), @"\s+", " ").Trim();
}
