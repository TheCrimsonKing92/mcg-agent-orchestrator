using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorStewardAdjudicationParserTests
{
    private const string Route = """
        {"kind":"route","targetTaskId":"planner-task","cause":"ContractClarification","text":"Correct the Planner format","evidenceReferences":["worker-output=receipt-1"]}
        """;

    [Xunit.Fact]
    public void Prose_then_fenced_route_parses_as_route()
    {
        var output = """
            Findings confirm this is a genuine formatting slip in the Planner output.

            ```json
            {"kind":"route","targetTaskId":"planner-task","cause":"ContractClarification","text":"Correct the Planner format","evidenceReferences":["worker-output=receipt-1"]}
            ```
            """;

        var result = ConductorStewardAdjudicationParser.Parse(output);

        Xunit.Assert.Equal("route", result.Kind);
        Xunit.Assert.Equal("planner-task", result.TargetTaskId);
        Xunit.Assert.Equal("ContractClarification", result.Cause);
        Xunit.Assert.Equal("Correct the Planner format", result.Text);
        Xunit.Assert.Equal(new[] { "worker-output=receipt-1" }, result.EvidenceReferences);
    }

    [Xunit.Fact]
    public void Last_fenced_object_wins_with_surrounding_prose()
    {
        var output = """
            Initial assessment:
            ```json
            {"kind":"no-action","reason":"initial"}
            ```
            Revised assessment:
            ```json
            {"kind":"route","targetTaskId":"planner-task","cause":"ContractClarification","text":"Correct the Planner format","evidenceReferences":["worker-output=receipt-1"]}
            ```
            Final assessment follows the route above.
            """;

        var result = ConductorStewardAdjudicationParser.Parse(output);

        Xunit.Assert.Equal("route", result.Kind);
        Xunit.Assert.Equal("planner-task", result.TargetTaskId);
        Xunit.Assert.Equal("ContractClarification", result.Cause);
    }

    [Xunit.Fact]
    public void Bare_json_still_parses()
    {
        var result = ConductorStewardAdjudicationParser.Parse(Route);

        Xunit.Assert.Equal("route", result.Kind);
        Xunit.Assert.Equal("planner-task", result.TargetTaskId);
    }

    [Xunit.Fact]
    public void Invalid_last_fence_does_not_fall_back_to_an_earlier_route()
    {
        var output = """
            ```json
            {"kind":"route","targetTaskId":"planner-task","cause":"ContractClarification","text":"Earlier route"}
            ```
            ```json
            {not json}
            ```
            """;

        var result = ConductorStewardAdjudicationParser.Parse(output);

        Xunit.Assert.Equal("no-action", result.Kind);
        Xunit.Assert.Equal("unparseable-output", result.Text);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Some prose without any result.")]
    [Xunit.InlineData("Some prose.\n```json\n{not json}\n```")]
    public void Invalid_result_is_unparseable_output(string output)
    {
        var result = ConductorStewardAdjudicationParser.Parse(output);

        Xunit.Assert.Equal("no-action", result.Kind);
        Xunit.Assert.Equal("unparseable-output", result.Text);
    }
}
