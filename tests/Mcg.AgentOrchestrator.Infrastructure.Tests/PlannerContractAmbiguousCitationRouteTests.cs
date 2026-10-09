using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its temporary filesystem tree and removes it in finally.
public sealed class PlannerContractAmbiguousCitationRouteTests : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("Program.cs")]
    [Xunit.InlineData("Program.cs:472")]
    public void Real_ambiguous_rejection_routes_to_both_full_paths(string citation)
    {
        var root = CreateTempDirectory();
        try
        {
            string[] candidates = ["src/First/Program.cs", "src/Second/Program.cs"];
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(root, candidate);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "// citation fixture");
            }

            var plan = PlanWithTargetCitation(citation);
            Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(
                plan, root, out _, out var rejection));
            Xunit.Assert.StartsWith($"target citation '{citation}' is ambiguous;", rejection);
            foreach (var candidate in candidates)
                Xunit.Assert.Contains($"'{candidate}'", rejection);

            var stderrPath = Path.Combine(root, "planner.stderr.txt");
            Xunit.Assert.True(PlannerOutputContract.TryPersistRejectionDiagnostic(
                stderrPath, rejection, out var persistenceDiagnostic), persistenceDiagnostic);
            var evidence = File.ReadAllText(stderrPath);
            Xunit.Assert.Contains(rejection, evidence);

            var trigger = new ConductorStewardTrigger(
                "goal", "planner-task", "sha", ConductorStewardTriggerKind.PlannerOutputContractRejected,
                DateTimeOffset.UnixEpoch, evidence, "", ["planner-rule=planner-output-contract-rejected"],
                ["Repair ambiguous citation"]);
            var output = new ConductorStewardDeterministicRoute().TryBuild(trigger, root);

            Xunit.Assert.NotNull(output);
            var route = ConductorStewardAdjudicationParser.Parse(output);
            Xunit.Assert.Equal("route", route.Kind);
            Xunit.Assert.Equal("ContractClarification", route.Cause);
            Xunit.Assert.Equal(trigger.TaskId, route.TargetTaskId);
            Xunit.Assert.Contains($"Offending ambiguous target citation: '{citation}'", route.Text);
            Xunit.Assert.Contains("Cite exactly one of these full repository-relative paths on the same mapping line:", route.Text);
            foreach (var candidate in candidates)
            {
                Xunit.Assert.Contains($"'{candidate}'", route.Text);
                Xunit.Assert.True(PlannerOutputContract.TryValidatePlan(
                    PlanWithTargetCitation(candidate), root, out _, out var diagnostic), diagnostic);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string PlanWithTargetCitation(string citation)
    {
        var plan = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        var headingStart = plan.IndexOf(heading, StringComparison.Ordinal);
        Xunit.Assert.True(headingStart >= 0, "Fixture must contain the target section.");
        var bodyStart = headingStart + heading.Length;
        var bodyEnd = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        Xunit.Assert.True(bodyEnd > bodyStart, "Fixture must contain the following section.");
        return plan[..bodyStart] + $"\n\n- Extend `{citation}` with focused ambiguity coverage.\n" + plan[bodyEnd..];
    }
}
