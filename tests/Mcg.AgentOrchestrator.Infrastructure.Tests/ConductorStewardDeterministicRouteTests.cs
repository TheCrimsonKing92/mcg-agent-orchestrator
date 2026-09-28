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
