using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: dispatch preparation only, with an independently owned repository.
public sealed class WorkerStandingRulesDispatchTests : WorkerDispatchTestSupport
{
    [Theory]
    [InlineData(AgentRole.Developer, true, 5)]
    [InlineData(AgentRole.Tester, true, 3)]
    [InlineData(AgentRole.Reviewer, true, 0)]
    [InlineData(AgentRole.Planner, true, 0)]
    [InlineData(AgentRole.Developer, false, 5)]
    [InlineData(AgentRole.Tester, false, 3)]
    [InlineData(AgentRole.Reviewer, false, 0)]
    [InlineData(AgentRole.Planner, false, 0)]
    public void PreparedPrompt_AddedRulesUseCompleteContextFiles(
        AgentRole role, bool typed, int expectedRuleCount)
    {
        var root = CreateSeededDispatchRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Update the label.", role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);

        var prepared = WorkerProfileDispatcher.PrepareTask(
            kernel, goal, task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(root, "prompts"), root, DateTimeOffset.UtcNow,
            providerName: typed ? "OpenAI" : "Anthropic",
            modelName: typed ? AgentCatalog.OpenAiSolSubscriptionModelAlias : "claude-sonnet-4-6");

        var prompt = File.ReadAllText(prepared.PromptPath);
        Assert.DoesNotContain(WorkerStandingRules.Heading, prompt);
        Assert.DoesNotContain(AgentOutputDirectives.PlannerStandingRules, prompt);
        var contextDirectory = Path.Combine(root, ".orchestrator-context", goal.Id.Value);
        var fileName = WorkerStandingRules.ContextFileNameForRole(role);
        if (fileName is null)
        {
            Assert.DoesNotContain(WorkerStandingRules.ContextFileName, prompt);
            Assert.DoesNotContain(WorkerStandingRules.PlannerContextFileName, prompt);
            return;
        }

        string rulesText;
        if (typed)
        {
            var receipt = prepared.Task.LastDispatch!.ContextPackageReceipt!;
            var section = Assert.Single(receipt.Sections.Where(section =>
                section.LogicalIdentity == $"context/{fileName}"));
            Assert.Equal(ContextDeliveryMode.MandatoryFile, section.DeliveryMode);
            Assert.NotNull(section.MandatoryRelativePath);
            Assert.Equal(1, prompt.Split($"MANDATORY READ: identity=context/{fileName};",
                StringSplitOptions.None).Length - 1);
            rulesText = File.ReadAllText(Path.Combine(root, section.MandatoryRelativePath!));
        }
        else
        {
            Assert.Equal(1, prompt.Split(WorkerStandingRules.ContextReference(role),
                StringSplitOptions.None).Length - 1);
            rulesText = File.ReadAllText(Path.Combine(contextDirectory, fileName));
        }

        if (role == AgentRole.Planner)
        {
            Assert.Contains(AgentOutputDirectives.PlannerStandingRules, rulesText);
            return;
        }

        Assert.Equal(1, rulesText.Split(WorkerStandingRules.Heading,
            StringSplitOptions.None).Length - 1);
        var ruleLines = rulesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(expectedRuleCount, ruleLines.Length);
        Assert.Equal(WorkerStandingRules.RulesForRole(role).Select(rule => $"- {rule}"), ruleLines);
        Assert.Equal(rulesText, File.ReadAllText(Path.Combine(contextDirectory, fileName)));
    }
}
