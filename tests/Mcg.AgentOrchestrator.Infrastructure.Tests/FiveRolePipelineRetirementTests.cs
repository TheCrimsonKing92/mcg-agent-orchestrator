using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: CLI work uses a unique workspace and in-memory kernel; no process or environment mutation.
public sealed class FiveRolePipelineRetirementTests : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("five-role")]
    [Xunit.InlineData("Five-Role")]
    public void Parse_RemovedPipeline_RejectsWithScoutGuidance(string value)
    {
        GoalIntakePipelineRequest? result = null;
        var error = Xunit.Assert.Throws<ArgumentException>(() =>
        {
            result = GoalIntakePipelineRequestParser.Parse(value);
        });
        Xunit.Assert.Null(result);
        Xunit.Assert.Contains("five-role", error.Message, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("scout", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void Parse_SupportedAndUnknownValues_PreservesFourValueContract()
    {
        const string allowed = "auto, scout, developer-reviewer, developer-only, test-first";
        Xunit.Assert.Equal(allowed, GoalIntakePipelineRequestParser.AllowedValues);
        foreach (var (value, expected) in new[]
        {
            ("auto", GoalIntakePipelineRequest.Auto),
            ("scout", GoalIntakePipelineRequest.Scout),
            ("developer-reviewer", GoalIntakePipelineRequest.DeveloperReviewer),
            ("developer-only", GoalIntakePipelineRequest.DeveloperOnly),
            ("test-first", GoalIntakePipelineRequest.TestFirst)
        })
        {
            var parsed = GoalIntakePipelineRequestParser.Parse(value);
            Xunit.Assert.Equal(expected, parsed);
            Xunit.Assert.Equal(value, parsed.ToCanonicalValue());
            Xunit.Assert.Equal(expected, GoalIntakePipelineRequestParser.Parse(value.ToUpperInvariant()));
        }
        var error = Xunit.Assert.Throws<ArgumentException>(() => GoalIntakePipelineRequestParser.Parse("two-role"));
        Xunit.Assert.Contains(allowed, error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ExecuteCommand_RemovedPipeline_RejectsEveryIntakeBeforeMutation()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            foreach (var args in new[]
            {
                new[] { "goal", "Update docs/usage.md", "--pipeline", "five-role" },
                new[] { "goal", "Update docs/usage.md", "--pipeline=five-role" },
                new[] { "goal", "Update docs/usage.md", "--from-backlog", "--create-goal", "--pipeline", "five-role", "--backlog-coverage", "full" },
                new[] { "backlog-intake", "Update CLI help label", "--create-goal", "--pipeline", "five-role", "--backlog-coverage", "full" }
            })
            {
                var kernel = new AgentOrchestratorKernel();
                IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                var providers = new InMemoryModelProviderRegistry([]);
                var profiles = WorkerProfileCatalog.Default();
                Goal? currentGoal = null;
                var error = Xunit.Assert.Throws<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
                    args, kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));
                Xunit.Assert.Contains("scout", error.Message, StringComparison.OrdinalIgnoreCase);
                Xunit.Assert.Empty(kernel.Goals);
                Xunit.Assert.Null(currentGoal);
            }
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact]
    public void Usage_RemovedPipeline_OffersOnlySupportedValues()
    {
        foreach (var usage in new[] { CliCommandHelp.GoalUsage, CliCommandHelp.BacklogIntakeUsage, CliCommandHelp.GoalReplaceUsage })
        {
            Xunit.Assert.DoesNotContain("five-role", usage, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.Contains("--pipeline <auto|scout|developer-reviewer|developer-only>", usage, StringComparison.Ordinal);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with one focused assertion.")]
    [Xunit.InlineData("Design the implementation approach for a new operator workflow.")]
    public void Intake_SupportedPipelines_NeverCreatesResearcherTasks(string objective)
    {
        Xunit.Assert.DoesNotContain("FiveRole", Enum.GetNames<GoalIntakePipeline>());
        Xunit.Assert.DoesNotContain("FiveRole", Enum.GetNames<GoalIntakePipelineRequest>());
        foreach (var pipeline in new GoalIntakePipeline?[]
        {
            null, GoalIntakePipeline.Scout, GoalIntakePipeline.DeveloperReviewer, GoalIntakePipeline.DeveloperOnly
        })
        {
            var plan = GoalObjectivePlanner.Build(objective, pipeline);
            Xunit.Assert.DoesNotContain(plan.TaskBoundaries, boundary => boundary.Role == AgentRole.Researcher);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, AgentCatalog.Default().Agents, plan);
            Xunit.Assert.DoesNotContain(goal.Tasks, task => task.RequiredRole == AgentRole.Researcher);
            if (pipeline is null or GoalIntakePipeline.Scout)
            {
                AgentRole[] expected = [AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer];
                Xunit.Assert.Equal(expected, plan.TaskBoundaries.Select(boundary => boundary.Role));
                Xunit.Assert.Equal(expected, goal.Tasks.Select(task => task.RequiredRole));
                Xunit.Assert.Equal("scout", plan.PipelineDecision.Workflow);
            }
        }
        var simpleKernel = new AgentOrchestratorKernel();
        var simpleGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(simpleKernel, AgentCatalog.Default().Agents, objective);
        Xunit.Assert.Equal(AgentRole.Developer, Xunit.Assert.Single(simpleGoal.Tasks).RequiredRole);
    }

    [Xunit.Fact]
    public void Docs_RemovedPipeline_DescribeScoutAndRetainResearcherCompatibility()
    {
        var root = FindRepositoryRoot();
        foreach (var path in new[] { "README.md", "docs/cli-reference.md", "docs/operator-runbook.md", ".agents/skills/orchestrator-dogfood/SKILL.md" })
        {
            var text = File.ReadAllText(Path.Combine(root, path));
            Xunit.Assert.DoesNotContain("--pipeline five-role", text, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.Contains("Scout is the only multi-role intake", text, StringComparison.Ordinal);
            Xunit.Assert.Contains("goals created earlier with a Researcher task still run", text, StringComparison.Ordinal);
        }
        var runbook = File.ReadAllText(Path.Combine(root, "docs/operator-runbook.md"));
        Xunit.Assert.DoesNotContain(runbook.Split(". "), sentence =>
            sentence.Contains("normal dogfood pipeline", StringComparison.OrdinalIgnoreCase) &&
            sentence.Contains("five-role", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void Preflight_LowConfidenceScope_RecommendsScoutPipeline()
    {
        var root = FindRepositoryRoot();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Design a new operator workflow.",
            [new TaskSpec(TaskId.New(), "Implement the workflow.", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var report = GoalReadinessPreflight.Build(goal, AgentCatalog.Default().Agents, root,
            resolveWorktree: (_, _) => root);
        var warning = Xunit.Assert.Single(report.Findings, finding => finding.Kind == "file-scope-low-confidence");
        Xunit.Assert.Contains("Scout pipeline", warning.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(GoalReadinessRecommendation.UseScoutGoal, report.Recommendation);
    }
}
