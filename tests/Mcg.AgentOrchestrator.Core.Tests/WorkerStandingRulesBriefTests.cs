using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: in-memory kernels and uniquely owned temporary context directories.
public sealed class WorkerStandingRulesBriefTests
{
    private const string SimpleDescription = "Update the label.";
    private const string ComplexDescription =
        "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.";

    [Theory]
    [InlineData(SimpleDescription, TaskComplexity.Simple, false)]
    [InlineData(SimpleDescription, TaskComplexity.Simple, true)]
    [InlineData(ComplexDescription, TaskComplexity.Complex, false)]
    [InlineData(ComplexDescription, TaskComplexity.Complex, true)]
    public void DeveloperBrief_BothInstructionPaths_ContainsFiveRulesOnce(
        string description, TaskComplexity complexity, bool fileAccess)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), description, AgentRole.Developer);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        Assert.Equal(complexity, TaskComplexityEstimator.Estimate(description, goal.Objective, task.RequiredRole));

        var context = fileAccess ? Directory.CreateTempSubdirectory("standing-rules-") : null;
        try
        {
            if (context is not null)
            {
                WorkerStandingRules.WriteContextArtifact(task.RequiredRole, context.FullName);
            }

            var brief = kernel.BuildTaskBrief(goal.Id, task.Id,
                workingDirectory: context?.FullName, contextDirectory: context?.FullName).Content;

            Assert.DoesNotContain("## PRACTICES", brief);
            var rules = ExtractRules(ReadRulesContent(brief, context?.FullName));
            Assert.Equal(5, rules.Length);
            AssertSharedRules(rules);
            Assert.Contains("Do not run dotnet test directly", rules[3]);
            Assert.Contains("report the test classes to run as `tests: deferred - ClassA, ClassB`", rules[3]);
            Assert.Contains("Before changing any message, reason or diagnostic text, search the tests for exact-equality assertions", rules[4]);
            Assert.Contains("if one exists put new information in a separate field instead of editing the asserted text", rules[4]);
        }
        finally
        {
            context?.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TesterBrief_StandingRules_ContainsExactlyTheThreeSharedRules(bool fileAccess)
    {
        var context = fileAccess ? Directory.CreateTempSubdirectory("standing-rules-") : null;
        try
        {
            var brief = BuildBrief(AgentRole.Tester, context?.FullName);
            var rules = ExtractRules(ReadRulesContent(brief, context?.FullName));
            Assert.Equal(3, rules.Length);
            AssertSharedRules(rules);
            Assert.DoesNotContain("dotnet test", string.Join('\n', rules));
            Assert.DoesNotContain("exact-equality assertions", string.Join('\n', rules));
        }
        finally
        {
            context?.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    [InlineData(AgentRole.Ideation)]
    public void OtherRoleBrief_StandingRules_OmitsBlock(AgentRole role)
    {
        Assert.DoesNotContain(WorkerStandingRules.Heading, BuildBrief(role));
    }

    [Fact]
    public void DeveloperBrief_PracticeMatchCap_StillContainsAllFiveStandingRules()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(),
            "Update classifier, dispatch, concurrency, operator ritual and state machine behavior.",
            AgentRole.Developer);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        Assert.Equal(4, kernel.MatchEngineeringPractices(goal, task).Count);

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.Contains("Matched practices: 4; cap 4;", brief);
        Assert.True(brief.IndexOf("## PRACTICES", StringComparison.Ordinal) <
            brief.IndexOf(WorkerStandingRules.Heading, StringComparison.Ordinal));
        var rules = ExtractRules(brief);
        Assert.Equal(5, rules.Length);
        AssertSharedRules(rules);
        Assert.Contains("Do not run dotnet test directly", rules[3]);
        Assert.Contains("exact-equality assertions", rules[4]);
    }

    [Fact]
    public void DeveloperSource_FileAccess_ReferencesCompleteRulesWithOneFixedLine()
    {
        var context = Directory.CreateTempSubdirectory("standing-rules-");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), SimpleDescription, AgentRole.Developer);
            var goal = kernel.CreateGoal("Maintain the label.", [task]);
            WorkerStandingRules.WriteContextArtifact(task.RequiredRole, context.FullName);

            var source = kernel.BuildTaskBriefSource(goal.Id, task.Id,
                workingDirectory: context.FullName, contextDirectory: context.FullName);

            var section = Assert.Single(source.Segments.Where(segment =>
                segment.Lines.Contains(WorkerStandingRules.ContextReference(AgentRole.Developer))));
            Assert.Null(section.TypedProjectionIdentity);
            Assert.Null(section.CollapsedLines);
            Assert.Single(section.Lines);
            var brief = source.ProjectLegacyMarkedTextV1(true).Content;
            AssertSharedRules(ExtractRules(ReadRulesContent(brief, context.FullName)));
            Assert.DoesNotContain(WorkerStandingRules.Heading,
                string.Join('\n', AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Developer)));
        }
        finally
        {
            context.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(SimpleDescription, false)]
    [InlineData(SimpleDescription, true)]
    [InlineData(ComplexDescription, false)]
    [InlineData(ComplexDescription, true)]
    public void PlannerBrief_BothInstructionPaths_ReferencesAddedRulesOnlyWithContext(
        string description, bool fileAccess)
    {
        var context = fileAccess ? Directory.CreateTempSubdirectory("standing-rules-") : null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), description, AgentRole.Planner);
            var goal = kernel.CreateGoal("Maintain the label.", [task]);
            if (context is not null)
            {
                WorkerStandingRules.WriteContextArtifact(task.RequiredRole, context.FullName);
            }

            var brief = kernel.BuildTaskBrief(goal.Id, task.Id,
                workingDirectory: context?.FullName, contextDirectory: context?.FullName).Content;
            if (context is null)
            {
                Assert.Contains(AgentOutputDirectives.PlannerStandingRules, brief);
            }
            else
            {
                Assert.Contains(WorkerStandingRules.ContextReference(AgentRole.Planner), brief);
                Assert.DoesNotContain(AgentOutputDirectives.PlannerStandingRules, brief);
                Assert.Contains(AgentOutputDirectives.PlannerStandingRules,
                    File.ReadAllText(Path.Combine(context.FullName, WorkerStandingRules.PlannerContextFileName)));
            }
            Assert.DoesNotContain(WorkerStandingRules.Heading, brief);
        }
        finally
        {
            context?.Delete(recursive: true);
        }
    }

    private static string ReadRulesContent(string brief, string? contextDirectory)
    {
        if (contextDirectory is null)
        {
            return brief;
        }

        Assert.DoesNotContain(WorkerStandingRules.Heading, brief);
        Assert.Equal(1, brief.Split(WorkerStandingRules.ContextReference(AgentRole.Developer),
            StringSplitOptions.None).Length - 1);
        return File.ReadAllText(Path.Combine(contextDirectory, WorkerStandingRules.ContextFileName));
    }

    private static string BuildBrief(AgentRole role, string? contextDirectory = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), SimpleDescription, role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        if (contextDirectory is not null)
        {
            WorkerStandingRules.WriteContextArtifact(task.RequiredRole, contextDirectory);
        }

        return kernel.BuildTaskBrief(goal.Id, task.Id,
            workingDirectory: contextDirectory, contextDirectory: contextDirectory).Content;
    }

    private static string[] ExtractRules(string brief)
    {
        Assert.Equal(1, brief.Split(WorkerStandingRules.Heading, StringSplitOptions.None).Length - 1);
        var lines = brief.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var headingIndex = Array.IndexOf(lines, WorkerStandingRules.Heading);
        Assert.True(headingIndex >= 0, "Standing rules must have their own heading line.");
        var rules = lines.Skip(headingIndex + 1).TakeWhile(line => line.Length > 0).ToArray();
        Assert.All(rules, rule => Assert.StartsWith("- ", rule));
        return rules;
    }

    private static void AssertSharedRules(string[] rules)
    {
        Assert.Contains("Name each new test file after its public test class.", rules[0]);
        Assert.Contains("new test class in a non-parallel xunit collection", rules[1]);
        Assert.Contains("must contain that collection's acceptance-lane substring in its class name", rules[1]);
        Assert.Contains("Tests never decide pass or fail on wall-clock time", rules[2]);
        Assert.Contains("bounded waits are hang-only and name the event that did not happen", rules[2]);
    }
}
