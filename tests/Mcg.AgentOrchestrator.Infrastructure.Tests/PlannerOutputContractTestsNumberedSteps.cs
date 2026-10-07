using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: filesystem access is confined to an independent temporary root per test.
public sealed class PlannerOutputContractTestsNumberedSteps : WorkerDispatchTestSupport
{
    // Verbatim receipt: 9797ec59-ae864b44-20261007055301.out.log, lines 108-113.
    private const string NumberedSteps = """
        1. Create the owner file.
        2. Remove the moved code from the verifier and add the delegating expression.
        3. Compile with `scripts/Invoke-WorkerBuildCheck.ps1` (a build, not a test run).
        4. Measure the verifier with `(Get-Content src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs).Count`, which matches `File.ReadLines`. Expect about 5351.
        5. Lower the ceiling row and add its comment.
        6. Add the two test files.
        """;

    private static readonly string[] Criteria =
        Enumerable.Range(1, 5).Select(number => $"Map requested behavior for criterion {number}.").ToArray();

    [Xunit.Fact]
    public void AcceptsNumberedStepsUnderIntegrationSeams()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(),
            "## Acceptance criteria mapping", MappingBody());
        plan = ReplaceSectionBody(plan, "## Integration seams", NumberedSteps);

        Xunit.Assert.True(PlannerOutputContract.TryValidatePlan(
            plan, root, out var validated, out var diagnostic, Criteria), diagnostic);
        Xunit.Assert.Equal(string.Empty, diagnostic);
        Xunit.Assert.Contains(NumberedSteps.ReplaceLineEndings("\n"),
            validated.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void SelectsDispositionMappingsAfterSameNumberedSteps()
    {
        var mappings = PlannerOutputContract.SelectCriterionMappings(NumberedSteps + "\n" + MappingBody());

        for (var criterion = 1; criterion <= 5; criterion++)
        {
            Xunit.Assert.Equal(MappingText(criterion), mappings[criterion]);
        }
        Xunit.Assert.Equal("Add the two test files.", mappings[6]);
    }

    [Xunit.Fact]
    public void RejectsMissingPlanDespiteEarlierNumberedStep()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        var mappings = MappingBody().Replace(MappingText(2),
            "criterion two. disposition=planned", StringComparison.Ordinal);
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(),
            "## Acceptance criteria mapping", NumberedSteps + "\n" + mappings);

        Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(
            plan, root, out _, out var diagnostic, Criteria));
        Xunit.Assert.Equal("acceptance criterion 2 with disposition=planned must include a non-empty plan", diagnostic);
    }

    [Xunit.Fact]
    public void StructuralQualityUsesDispositionMappingsAfterSteps()
    {
        var mappingsOnly = ReplaceSectionBody(PlannerContractPlanFixture(),
            "## Acceptance criteria mapping", MappingBody());
        var stepsFirst = ReplaceSectionBody(PlannerContractPlanFixture(),
            "## Acceptance criteria mapping", NumberedSteps + "\n" + MappingBody());

        var expected = PlannerOutputContract.EvaluateStructuralQuality(mappingsOnly, 5);
        var actual = PlannerOutputContract.EvaluateStructuralQuality(stepsFirst, 5);

        Xunit.Assert.Equal(5, expected.ConcreteOwningSeams);
        Xunit.Assert.Equal(expected, actual);
    }

    [Xunit.Fact]
    public void KeepsFirstMappingWithinEachGrammarPreference()
    {
        const string firstDisposition = "subject. DiSpOsItIoN \t= planned; plan=Keep the first declared disposition.";
        var body = string.Join("\n",
            "1. First historical mapping.", "1. Second historical mapping.",
            "2. Earlier numbered implementation step.", "2. maps to " + firstDisposition,
            "2. disposition=planned; plan=Later disposition must not win.",
            "2. Later numbered implementation step.");

        var mappings = PlannerOutputContract.SelectCriterionMappings(body);

        Xunit.Assert.Equal("First historical mapping.", mappings[1]);
        Xunit.Assert.Equal(firstDisposition, mappings[2]);
    }

    [Xunit.Fact]
    public void StructuralQualityStillSkipsBlankAndNonPositiveMappings()
    {
        var expectedPlan = ReplaceSectionBody(PlannerContractPlanFixture(),
            "## Acceptance criteria mapping", "1. " + MappingText(1));
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(),
            "## Acceptance criteria mapping", "0. Ignore a non-positive mapping.\n1. \n1. " + MappingText(1));

        Xunit.Assert.Equal(PlannerOutputContract.EvaluateStructuralQuality(expectedPlan, 1),
            PlannerOutputContract.EvaluateStructuralQuality(plan, 1));
    }

    private static string MappingText(int criterion) =>
        $"`seed.txt` criterion {criterion}. disposition=planned; plan=Developer owns this integration seam and Acceptance executes TEST-VERIFIABLE coverage with a stop on missing evidence.";

    private static string MappingBody() =>
        string.Join("\n", Enumerable.Range(1, 5).Select(number => $"{number}. maps to {MappingText(number)}"));

    private static string ReplaceSectionBody(string plan, string heading, string body)
    {
        plan = plan.ReplaceLineEndings("\n");
        var start = plan.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var end = plan.IndexOf("\n## ", start, StringComparison.Ordinal);
        return plan[..start] + "\n\n" + body + "\n" + plan[end..];
    }
}
