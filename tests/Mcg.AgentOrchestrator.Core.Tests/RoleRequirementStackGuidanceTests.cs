using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: pure transformations and in-memory kernels with a fixed clock.
public sealed class RoleRequirementStackGuidanceTests
{
    private const string SimpleDescription = "Update the label.";
    private const string ComplexDescription =
        "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.";

    private static readonly (string Source, string Go, string? Neutral)[] DeveloperTable =
    [
        ("- Name the failing test in `tests: deferred`; never run tests.",
            "- Report each go command and its result in `tests`.",
            "- Report each command and its result in `tests`."),
        ("- Build changed projects with the worker build check.",
            "- Build and test with the go commands in the Standing Rules.",
            "- Build and test with the commands in the Standing Rules.")
    ];

    private static readonly (string Source, string Go, string? Neutral)[] TesterTable =
    [
        ("- Name the test classes to run in evidence_request; never run tests.",
            "- Name the packages and tests to run in evidence_request.",
            "- Name the tests to run in evidence_request."),
        ("- Build only with the worker build check; do not modify source files.",
            "- Build only with `go build ./...`; do not modify source files.",
            "- Build only with the project's own build command; do not modify source files."),
        ("- Conductor-run selections use FullyQualifiedName~Class or Name~Method-symbol syntax and reject DisplayName text and a bare class name.",
            "- Go selections: test_project is a package directory relative to go.mod; test_class is test names joined by `|`, such as `TestParse|TestFormat`.",
            null)
    ];

    public static IEnumerable<object[]> RoleComplexities() =>
        from role in Enum.GetValues<AgentRole>()
        from complexity in new[] { TaskComplexity.Simple, TaskComplexity.Complex }
        select new object[] { role, complexity };

    [Theory]
    [InlineData(AgentRole.Developer, TaskComplexity.Simple)]
    [InlineData(AgentRole.Developer, TaskComplexity.Complex)]
    [InlineData(AgentRole.Tester, TaskComplexity.Simple)]
    [InlineData(AgentRole.Tester, TaskComplexity.Complex)]
    public void Apply_SubstitutesExactTableAndPreservesOtherLines(AgentRole role, TaskComplexity complexity)
    {
        var input = SdlcRolePromptRequirements.Build(role, complexity);
        var table = role == AgentRole.Developer ? DeveloperTable : TesterTable;
        foreach (var row in table)
            Assert.Equal(1, input.Count(line => line == row.Source));

        var go = RoleRequirementStackGuidance.Apply(input, ProjectStack.Go);
        var neutral = RoleRequirementStackGuidance.Apply(input, null);
        Assert.NotSame(input, go);
        Assert.NotSame(input, neutral);
        Assert.Equal(input.Count, go.Count);
        Assert.Equal(input.Count - (role == AgentRole.Tester ? 1 : 0), neutral.Count);

        var expectedNeutral = new List<string>();
        for (var i = 0; i < input.Count; i++)
        {
            var rowIndex = Array.FindIndex(table, row => row.Source == input[i]);
            Assert.Equal(rowIndex < 0 ? input[i] : table[rowIndex].Go, go[i]);
            var neutralLine = rowIndex < 0 ? input[i] : table[rowIndex].Neutral;
            if (neutralLine is not null)
                expectedNeutral.Add(neutralLine);
        }
        Assert.Equal(expectedNeutral, neutral);
        Assert.True(string.Join('\n', go).Length <= string.Join('\n', input).Length);
        Assert.True(string.Join('\n', neutral).Length <= string.Join('\n', input).Length);
    }

    [Theory]
    [MemberData(nameof(RoleComplexities))]
    public void Apply_DotnetRetainsIdentityAndUnaffectedRolesRetainText(AgentRole role, TaskComplexity complexity)
    {
        var input = SdlcRolePromptRequirements.Build(role, complexity);
        Assert.Same(input, RoleRequirementStackGuidance.Apply(input, ProjectStack.Dotnet));
        if (role is AgentRole.Developer or AgentRole.Tester)
            return;

        AssertUnchangedForeignLines(input);
    }

    [Fact]
    public void Apply_ScoutRetainsIdentityForDotnetAndTextForForeignStacks()
    {
        var input = SdlcRolePromptRequirements.BuildScout(TaskComplexity.Complex, false);
        Assert.Same(input, RoleRequirementStackGuidance.Apply(input, ProjectStack.Dotnet));
        AssertUnchangedForeignLines(input);
    }

    [Fact]
    public void Apply_DoesNotMatchTrailingWhitespaceOrDifferentCase()
    {
        string[] input = [DeveloperTable[1].Source + " ", TesterTable[1].Source.ToUpperInvariant()];
        AssertUnchangedForeignLines(input);
    }

    [Theory]
    [InlineData(AgentRole.Developer, ProjectStack.Go)]
    [InlineData(AgentRole.Tester, ProjectStack.Go)]
    [InlineData(AgentRole.Tester, null)]
    public void Kernel_UsesStackForRequirementsAndInlineStandingRules(AgentRole role, ProjectStack? stack)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var task = new TaskSpec(TaskId.New(), SimpleDescription, role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, role);
        var source = kernel.BuildTaskBriefSource(goal.Id, task.Id, roleRequirementStack: stack);
        var requirements = Assert.Single(source.Segments.Where(segment =>
            segment.Lines.FirstOrDefault() == $"## {role} Requirements"));
        var expected = RoleRequirementStackGuidance.Apply(SdlcRolePromptRequirements.Build(role, complexity), stack);
        Assert.Equal(expected.Append(string.Empty), requirements.Lines);

        var standingRules = Assert.Single(source.Segments.Where(segment =>
            segment.Lines.FirstOrDefault() == WorkerStandingRules.Heading));
        Assert.Equal(WorkerStandingRules.RenderBriefSection(role, null, stack), standingRules.Lines);
        if (stack is null)
            Assert.DoesNotContain(requirements.Lines, line => line.Contains("FullyQualifiedName~Class", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AgentRole.Developer, TaskComplexity.Simple)]
    [InlineData(AgentRole.Developer, TaskComplexity.Complex)]
    [InlineData(AgentRole.Tester, TaskComplexity.Simple)]
    [InlineData(AgentRole.Tester, TaskComplexity.Complex)]
    [InlineData(AgentRole.Reviewer, TaskComplexity.Simple)]
    [InlineData(AgentRole.Reviewer, TaskComplexity.Complex)]
    [InlineData(AgentRole.Planner, TaskComplexity.Simple)]
    [InlineData(AgentRole.Planner, TaskComplexity.Complex)]
    public void Kernel_OmittedStackEqualsExplicitDotnet(AgentRole role, TaskComplexity complexity)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var task = new TaskSpec(TaskId.New(),
            complexity == TaskComplexity.Simple ? SimpleDescription : ComplexDescription, role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        Assert.Equal(complexity, TaskComplexityEstimator.Estimate(task.Description, goal.Objective, role));

        var omitted = kernel.BuildTaskBriefSource(goal.Id, task.Id)
            .ProjectLegacyMarkedTextV1(emitTypedSourceBoundaries: false).Content;
        var dotnet = kernel.BuildTaskBriefSource(goal.Id, task.Id, roleRequirementStack: ProjectStack.Dotnet)
            .ProjectLegacyMarkedTextV1(emitTypedSourceBoundaries: false).Content;
        Assert.Equal(omitted, dotnet);

        var lines = dotnet.ReplaceLineEndings("\n").Split('\n');
        if (role is AgentRole.Developer or AgentRole.Tester)
        {
            foreach (var row in role == AgentRole.Developer ? DeveloperTable : TesterTable)
                Assert.Equal(1, lines.Count(line => line == row.Source));
        }
        foreach (var row in DeveloperTable.Concat(TesterTable))
        {
            Assert.DoesNotContain(row.Go, lines);
            if (row.Neutral is not null)
                Assert.DoesNotContain(row.Neutral, lines);
        }
    }

    private static void AssertUnchangedForeignLines(IReadOnlyList<string> input)
    {
        foreach (var stack in new ProjectStack?[] { ProjectStack.Go, null })
        {
            var output = RoleRequirementStackGuidance.Apply(input, stack);
            Assert.NotSame(input, output);
            Assert.Equal(input, output);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
    }
}
