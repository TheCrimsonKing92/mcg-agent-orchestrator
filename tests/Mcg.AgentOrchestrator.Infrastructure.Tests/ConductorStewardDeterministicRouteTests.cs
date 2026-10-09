using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorStewardDeterministicRouteTests
{
    [Xunit.Fact]
    public void Known_planner_reasons_produce_one_contract_route()
    {
        var trigger = Trigger("[orchestrator Planner output contract rejection] missing required section 'integration seams'; " +
            "criterion 4 is unmapped; a line for criterion 4 was found but was not parsed as a mapping: '4. maps to tests'; " +
            "acceptance criterion 2 with disposition=planned must include a non-empty plan");

        var output = new ConductorStewardDeterministicRoute().TryBuild(trigger, "unused");

        Xunit.Assert.NotNull(output);
        var route = ConductorStewardAdjudicationParser.Parse(output);
        Xunit.Assert.Equal("route", route.Kind);
        Xunit.Assert.Equal("ContractClarification", route.Cause);
        Xunit.Assert.Equal(trigger.TaskId, route.TargetTaskId);
        Xunit.Assert.Contains("integration seams", route.Text);
        Xunit.Assert.Contains("4. maps to tests", route.Text);
        Xunit.Assert.Contains("criterion 2", route.Text);
        Xunit.Assert.Contains("same", route.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void Wildcard_citation_lists_injected_tracked_files()
    {
        var lister = new FixedFiles();
        var output = new ConductorStewardDeterministicRoute(lister).TryBuild(Trigger(
            "target citation 'src/Foo*.cs' does not exist and is not marked as a new file; source span [1..12). " +
            "Offending citation: 'src/Foo*.cs'"), "unused");

        var route = ConductorStewardAdjudicationParser.Parse(output!);
        Xunit.Assert.Equal("Foo", lister.Stem);
        Xunit.Assert.Contains("src/Foo*.cs", route.Text);
        Xunit.Assert.Contains("src/FooOne.cs", route.Text);
        Xunit.Assert.Contains("src/FooTwo.cs", route.Text);
        Xunit.Assert.Contains("repository-relative", route.Text);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Program.cs")]
    [Xunit.InlineData("Program.cs:472")]
    public void Ambiguous_citation_uses_diagnostic_candidates_without_file_lookup(string citation)
    {
        var output = new ConductorStewardDeterministicRoute(new ThrowingFiles()).TryBuild(Trigger(
            $"target citation '{citation}' is ambiguous; at least these repository files match: " +
            "'src/First/Program.cs', 'src/Second/Program.cs'; source span [1..12).\n" +
            $"Offending citation: '{citation}' offending plan line: 'do not treat this as a candidate'"), "unused");

        Xunit.Assert.NotNull(output);
        var route = ConductorStewardAdjudicationParser.Parse(output);
        Xunit.Assert.Contains($"Offending ambiguous target citation: '{citation}'", route.Text);
        Xunit.Assert.Contains("Cite exactly one of these full repository-relative paths on the same mapping line: 'src/First/Program.cs', 'src/Second/Program.cs'.", route.Text);
        Xunit.Assert.Contains("Cite a concrete repository-relative file path that exists at HEAD", route.Text);
        Xunit.Assert.Contains("Write the disposition and non-empty one-sentence plan summary on the same mapping line", route.Text);
        Xunit.Assert.DoesNotContain("do not treat this as a candidate", route.Text);
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData("unquoted candidates")]
    [Xunit.InlineData("' '")]
    public void Ambiguous_citation_without_parseable_candidates_falls_back(string candidates)
    {
        var output = new ConductorStewardDeterministicRoute().TryBuild(Trigger(
            "target citation 'Program.cs' is ambiguous; at least these repository files match: " +
            candidates + "; source span [1..12).\nOffending citation: 'Program.cs'"), "unused");

        Xunit.Assert.Null(output);
    }

    [Xunit.Fact]
    public void Ambiguous_citation_in_mixed_unknown_batch_still_routes()
    {
        var output = new ConductorStewardDeterministicRoute().TryBuild(Trigger(
            "unrecognized rejection\n" +
            "target citation 'Program.cs' is ambiguous; at least these repository files match: " +
            "'src/First/Program.cs', 'src/Second/Program.cs'; source span [1..12)."), "unused");

        Xunit.Assert.NotNull(output);
        var route = ConductorStewardAdjudicationParser.Parse(output);
        Xunit.Assert.Contains("'Program.cs'", route.Text);
        Xunit.Assert.Contains("src/First/Program.cs", route.Text);
        Xunit.Assert.Contains("src/Second/Program.cs", route.Text);
    }

    [Xunit.Fact]
    public void Ambiguous_citations_append_after_existing_missing_path_correction()
    {
        var output = new ConductorStewardDeterministicRoute(new FixedFiles()).TryBuild(Trigger(
            "target citation 'src/Foo*.cs' does not exist and is not marked as a new file;\n" +
            "target citation 'Program.cs' is ambiguous; at least these repository files match: " +
            "'src/First/Program.cs', 'src/Second/Program.cs';\n" +
            "target citation 'Other.cs' is ambiguous; at least these repository files match: " +
            "'src/First/Other.cs', 'src/Second/Other.cs';"), "unused");

        Xunit.Assert.NotNull(output);
        var route = ConductorStewardAdjudicationParser.Parse(output);
        const string originalCorrection = "Offending target citation: 'src/Foo*.cs'. " +
            "Cite a concrete repository-relative file path that exists at HEAD, without wildcards, or explicitly mark the path as new. " +
            "Write the disposition and non-empty one-sentence plan summary on the same mapping line: N. maps to <subject>. disposition=planned; plan=<non-empty one-sentence summary>. " +
            "Tracked files matching the cited stem 'Foo': src/FooOne.cs, src/FooTwo.cs.";
        Xunit.Assert.StartsWith(originalCorrection + " Offending ambiguous target citation: 'Program.cs'.", route.Text);
        Xunit.Assert.Contains("'src/First/Program.cs', 'src/Second/Program.cs'", route.Text);
        Xunit.Assert.Contains("Offending ambiguous target citation: 'Other.cs'.", route.Text);
        Xunit.Assert.Contains("'src/First/Other.cs', 'src/Second/Other.cs'", route.Text);
    }

    [Xunit.Fact]
    public void Empty_ambiguous_candidates_preserve_other_corrections()
    {
        var output = new ConductorStewardDeterministicRoute().TryBuild(Trigger(
            "missing required section 'integration seams';\n" +
            "target citation 'Program.cs' is ambiguous; at least these repository files match: ;"), "unused");

        Xunit.Assert.NotNull(output);
        var route = ConductorStewardAdjudicationParser.Parse(output);
        Xunit.Assert.Contains("Missing required section 'integration seams'", route.Text);
        Xunit.Assert.DoesNotContain("Offending ambiguous target citation", route.Text);
    }

    [Xunit.Fact]
    public void Unmatched_reason_and_lister_failure_fall_back()
    {
        Xunit.Assert.Null(new ConductorStewardDeterministicRoute().TryBuild(Trigger("unknown diagnostic"), "unused"));
        Xunit.Assert.Null(new ConductorStewardDeterministicRoute(new ThrowingFiles()).TryBuild(Trigger(
            "target citation 'src/Foo*.cs' does not exist and is not marked as a new file"), "unused"));
    }

    private static ConductorStewardTrigger Trigger(string evidence) => new(
        "goal", "task", "sha", ConductorStewardTriggerKind.PlannerOutputContractRejected,
        DateTimeOffset.UtcNow, evidence, "", ["planner-rule=planner-output-contract-rejected"], ["Repair failure"]);

    private sealed class FixedFiles : IConductorStewardTrackedFileLister
    {
        internal string? Stem { get; private set; }
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem)
        {
            Stem = stem;
            return ["src/FooOne.cs", "src/FooTwo.cs"];
        }
    }

    private sealed class ThrowingFiles : IConductorStewardTrackedFileLister
    {
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem) => throw new IOException("git unavailable");
    }
}
