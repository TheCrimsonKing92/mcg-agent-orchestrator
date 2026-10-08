using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalDagPlanTests
{
    // ── Parse: valid fenced JSON ──────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDagPlan_Parse_ValidFencedJson_ReturnsNodes")]
    public void GoalDagPlan_Parse_ValidFencedJson_ReturnsNodes()
    {
        var json = """
            ```json
            [{"id":"g1","objective":"Set up data model","dependsOn":[]},{"id":"g2","objective":"Implement service","dependsOn":["g1"]}]
            ```
            """;
        var plan = GoalDagDecompositionPlanner.Parse("Build the system", json);

        Assert.True(plan.IsValid);
        Assert.True(plan.Nodes.Count == 2);
        Assert.True(plan.Nodes[0].Id == "g1");
        Assert.True(plan.Nodes[1].Id == "g2");
        Assert.True(plan.Nodes[1].DependsOn.Contains("g1"));
    }

    // ── Parse: missing fenced block ───────────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDagPlan_Parse_NoFencedJson_ReturnsError")]
    public void GoalDagPlan_Parse_NoFencedJson_ReturnsError()
    {
        var plan = GoalDagDecompositionPlanner.Parse("Build the system", "Here is my plan: step 1, step 2.");

        Assert.False(plan.IsValid);
        Assert.True(plan.ValidationErrors.Any(e => e.Contains("fenced JSON", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Parse: self-dependency rejected ──────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDagPlan_Parse_SelfDependency_ReturnsError")]
    public void GoalDagPlan_Parse_SelfDependency_ReturnsError()
    {
        var json = """
            ```json
            [{"id":"g1","objective":"Do something","dependsOn":["g1"]}]
            ```
            """;
        var plan = GoalDagDecompositionPlanner.Parse("Build the system", json);

        Assert.False(plan.IsValid);
        Assert.True(plan.ValidationErrors.Any(e => e.Contains("g1", StringComparison.Ordinal) &&
            e.Contains("itself", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Parse: cycle rejected ─────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDagPlan_Parse_Cycle_ReturnsError")]
    public void GoalDagPlan_Parse_Cycle_ReturnsError()
    {
        var json = """
            ```json
            [{"id":"g1","objective":"A","dependsOn":["g2"]},{"id":"g2","objective":"B","dependsOn":["g1"]}]
            ```
            """;
        var plan = GoalDagDecompositionPlanner.Parse("Build the system", json);

        Assert.False(plan.IsValid);
        Assert.True(plan.ValidationErrors.Any(e => e.Contains("cycle", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Parse: unresolved reference rejected ─────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDagPlan_Parse_UnresolvedReference_ReturnsError")]
    public void GoalDagPlan_Parse_UnresolvedReference_ReturnsError()
    {
        var json = """
            ```json
            [{"id":"g1","objective":"Do something","dependsOn":["g99"]}]
            ```
            """;
        var plan = GoalDagDecompositionPlanner.Parse("Build the system", json);

        Assert.False(plan.IsValid);
        Assert.True(plan.ValidationErrors.Any(e => e.Contains("g99", StringComparison.Ordinal)));
    }

    // ── CLI: plan preview does not create goals ───────────────────────────────

    [Xunit.Fact(DisplayName = "Cli_Plan_Preview_ShowsNodesWithoutCreatingGoals")]
    public void Cli_Plan_Preview_ShowsNodesWithoutCreatingGoals()
    {
        var (kernel, workspace, agents, providers) = BuildTestContext(TwoNodeJson);
        using var samples = PlanSampleProviderBridge.Use(providers);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["plan", "Implement data model then service layer"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Assert.True(kernel.Goals.Count == 0);
        Assert.True(output.Contains("g1", StringComparison.Ordinal));
        Assert.True(output.Contains("g2", StringComparison.Ordinal));
    }

    // ── CLI: confirm-plan creates two goals with dependency edge ─────────────

    [Xunit.Fact(DisplayName = "Cli_Plan_ConfirmPlan_TwoNodeChain_CreatesGoalsWithDependency")]
    public void Cli_Plan_ConfirmPlan_TwoNodeChain_CreatesGoalsWithDependency()
    {
        var (kernel, workspace, agents, providers) = BuildTestContext(TwoNodeJson);
        using var samples = PlanSampleProviderBridge.Use(providers);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["plan", "Implement data model then service layer", "--confirm-plan"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Assert.True(kernel.Goals.Count == 2);

        var g1 = kernel.Goals.Single(g => g.Objective.Contains("data model", StringComparison.OrdinalIgnoreCase));
        var g2 = kernel.Goals.Single(g => g.Objective.Contains("service", StringComparison.OrdinalIgnoreCase));
        Assert.True(g2.DependsOn.Contains(g1.Id));
    }

    // ── CLI: confirm-plan with cycle throws before creating any goals ─────────

    [Xunit.Fact(DisplayName = "Cli_Plan_ConfirmPlan_CyclePlan_ThrowsInvalidOperation")]
    public void Cli_Plan_ConfirmPlan_CyclePlan_ThrowsInvalidOperation()
    {
        var cycleJson = """
            ```json
            [{"id":"g1","objective":"A","dependsOn":["g2"]},{"id":"g2","objective":"B","dependsOn":["g1"]}]
            ```
            """;
        var (kernel, workspace, agents, providers) = BuildTestContext(cycleJson);
        using var samples = PlanSampleProviderBridge.Use(providers);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Assert.ThrowsAny<InvalidOperationException>(() =>
            CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["plan", "Implement data model then service layer", "--confirm-plan"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));

        Assert.True(kernel.Goals.Count == 0);
        Assert.True(ex.Message.Contains("validation error", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_Confirm_CreatesDormantParentAndActiveChildren")]
    public void CliPlanSliceBatchConfirmCreatesDormantParentAndActiveChildren()
    {
        var (kernel, workspace, agents, providers) = BuildSliceBatchTestContext(ThreeSliceBatchJson);
        using var samples = PlanSampleProviderBridge.Use(providers);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["plan", "Implement three disjoint feature slices", "--slice-batch", "--confirm-plan"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Assert.Equal(4, kernel.Goals.Count);
        var parent = Assert.Single(kernel.Goals.Where(goal => goal.SliceBatchParentId is null));
        Assert.Equal(parent.Id, currentGoal!.Id);
        Assert.Equal([AgentRole.Reviewer], parent.Tasks.Select(task => task.RequiredRole));

        var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
        Assert.Equal(3, children.Length);
        Assert.Equal(GoalStatus.Draft, parent.Status);
        Assert.All(parent.Tasks, task => Assert.Null(task.AssignedAgentId));
        Assert.All(kernel.Goals, goal =>
        {
            Assert.Empty(goal.DependsOn);
            Assert.True(GoalRefinementWorkCoordinator.HasPendingWork(goal));
        });
        Assert.All(children, child =>
        {
            Assert.Equal(GoalStatus.Active, child.Status);
            Assert.Equal([AgentRole.Developer, AgentRole.Reviewer], child.Tasks.Select(task => task.RequiredRole));
            var task = Assert.Single(child.Tasks.Where(task => task.RequiredRole == AgentRole.Developer));
            Assert.NotNull(task.AssignedAgentId);
        });

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        Assert.All(
            restored.Goals.Where(goal => goal.Id != parent.Id),
            child => Assert.Equal(parent.Id, child.SliceBatchParentId));

        var intents = children.Select(child =>
        {
            var task = Assert.Single(child.Tasks.Where(task => task.RequiredRole == AgentRole.Developer));
            var scope = GoalFileScopeInference.ForScheduling(child, task);
            Assert.Equal(RepositoryScopeConfidence.Precise, scope.Confidence);
            return new ParallelExecutionIntent(
                task.Id.Value,
                child.Id.Value,
                scope.Includes,
                ScopeConfidence: scope.Confidence);
        }).ToArray();
        var executionPlan = ParallelExecutionPlanner.Build(intents);
        Assert.Equal(3, Assert.Single(executionPlan.Batches).IntentIds.Count);
        Assert.All(executionPlan.Decisions, decision =>
        {
            Assert.Equal(1, decision.BatchNumber);
            Assert.Equal(ParallelExecutionDisposition.Concurrent, decision.Disposition);
        });

        Assert.Contains($"parent {parent.Id.Value}", output, StringComparison.Ordinal);
        Assert.All(children, child => Assert.Contains(child.Id.Value, output, StringComparison.Ordinal));
        Assert.Contains("child execution is enabled", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("each child currently runs its own acceptance gate", output, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void CliPlanSliceBatchWithoutDeveloperRejectsAtomically()
    {
        var (kernel, workspace, configuredAgents, providers) = BuildSliceBatchTestContext(ThreeSliceBatchJson);
        using var samples = PlanSampleProviderBridge.Use(providers);
        IReadOnlyList<AgentDefinition> agents = configuredAgents
            .Where(agent => agent.Role != AgentRole.Developer)
            .ToArray();
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["plan", "Implement three disjoint feature slices", "--slice-batch", "--confirm-plan"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));

        Assert.Empty(kernel.Goals);
        Assert.Contains("available Developer agent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No goal was created", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_Preview_IsDormantAndDoesNotMutate")]
    public void CliPlanSliceBatchPreviewIsDormantAndDoesNotMutate()
    {
        var (kernel, workspace, configuredAgents, providers) = BuildSliceBatchTestContext(ThreeSliceBatchJson);
        using var samples = PlanSampleProviderBridge.Use(providers);
        IReadOnlyList<AgentDefinition> agents = configuredAgents
            .Where(agent => agent.Role != AgentRole.Developer)
            .ToArray();
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["plan", "Implement three disjoint feature slices", "--slice-batch"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Assert.Empty(kernel.Goals);
        Assert.Contains("Dormant slice-batch intake preview", output, StringComparison.Ordinal);
        Assert.Contains("--slice-batch --confirm-plan", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_InvalidNodeCount_RejectedAtomically")]
    public void CliPlanSliceBatchInvalidNodeCountRejectedAtomically()
    {
        var (exception, kernel) = ConfirmInvalidSliceBatch(
            SliceBatchJson(SliceObjective("only", "src/FeatureA/A.cs")));

        Assert.Empty(kernel.Goals);
        Assert.Contains("found 1", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CliPlanSliceBatchUnknownDependencyRejectedAtomically()
    {
        var json = SliceBatchJson(
            SliceObjective("g1", "src/FeatureA/A.cs"),
            SliceObjective("g2", "src/FeatureB/B.cs", "missing"));
        var (exception, kernel) = ConfirmInvalidSliceBatch(json);

        Assert.Empty(kernel.Goals);
        Assert.Contains("references unknown node 'missing'", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_MissingScope_RejectedAtomically")]
    public void CliPlanSliceBatchMissingScopeRejectedAtomically()
    {
        var json = SliceBatchJson(
            "{\"id\":\"missing\",\"objective\":\"Implement the missing slice\",\"dependsOn\":[]}",
            SliceObjective("precise", "src/FeatureB/B.cs"));
        var (exception, kernel) = ConfirmInvalidSliceBatch(json);

        Assert.Empty(kernel.Goals);
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Target files/scopes:", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_NonPreciseScope_RejectedAtomically")]
    public void CliPlanSliceBatchNonPreciseScopeRejectedAtomically()
    {
        var nonPrecise = "{\"id\":\"unknown\",\"objective\":\"Implement uncertain scope.\\n\\nTarget files/scopes:\\nScope confidence: unknown\\nIncludes:\\n- src/FeatureA/A.cs\",\"dependsOn\":[]}";
        var json = SliceBatchJson(nonPrecise, SliceObjective("precise", "src/FeatureB/B.cs"));
        var (exception, kernel) = ConfirmInvalidSliceBatch(json);

        Assert.Empty(kernel.Goals);
        Assert.Contains("unknown", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Scope confidence: precise", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_OverlappingScopes_RejectedAtomically")]
    public void CliPlanSliceBatchOverlappingScopesRejectedAtomically()
    {
        var json = SliceBatchJson(
            SliceObjective("directory", "src/FeatureA"),
            SliceObjective("file", "src/FeatureA/A.cs"));
        var (exception, kernel) = ConfirmInvalidSliceBatch(json);

        Assert.Empty(kernel.Goals);
        Assert.Contains("directory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("file", exception.Message, StringComparison.Ordinal);
        Assert.Contains("src/FeatureA/A.cs", exception.Message, StringComparison.Ordinal);
    }

    // ── Best-of-N selection ──────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "BestOfN_AllValid_PicksFewestNodes")]
    public void BestOfN_AllValid_PicksFewestNodes()
    {
        var threeNodePlan = GoalDagDecompositionPlanner.Parse("dir", """
            ```json
            [{"id":"g1","objective":"A","dependsOn":[]},{"id":"g2","objective":"B","dependsOn":["g1"]},{"id":"g3","objective":"C","dependsOn":["g2"]}]
            ```
            """);
        var twoNodePlan = GoalDagDecompositionPlanner.Parse("dir", """
            ```json
            [{"id":"g1","objective":"A","dependsOn":[]},{"id":"g2","objective":"B","dependsOn":["g1"]}]
            ```
            """);

        var selected = GoalDagDecompositionPlanner.SelectBestOfN([threeNodePlan, twoNodePlan]);

        Assert.True(selected.IsValid);
        Assert.True(selected.Nodes.Count == 2);
    }

    [Xunit.Fact(DisplayName = "BestOfN_FirstInvalidSecondValid_PicksValid")]
    public void BestOfN_FirstInvalidSecondValid_PicksValid()
    {
        var invalidPlan = GoalDagDecompositionPlanner.Parse("dir", "no fenced json here");
        var validPlan = GoalDagDecompositionPlanner.Parse("dir", """
            ```json
            [{"id":"g1","objective":"A","dependsOn":[]}]
            ```
            """);

        var selected = GoalDagDecompositionPlanner.SelectBestOfN([invalidPlan, validPlan]);

        Assert.True(selected.IsValid);
        Assert.True(selected.Nodes.Count == 1);
    }

    [Xunit.Fact(DisplayName = "BestOfN_NoneValid_ReturnsFirstErrors")]
    public void BestOfN_NoneValid_ReturnsFirstErrors()
    {
        var invalid1 = GoalDagDecompositionPlanner.Parse("dir", "no json here");
        var invalid2 = GoalDagDecompositionPlanner.Parse("dir", "also no json");

        var selected = GoalDagDecompositionPlanner.SelectBestOfN([invalid1, invalid2]);

        Assert.False(selected.IsValid);
        Assert.True(selected.ValidationErrors.Count > 0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void FileDirection_MatchesPositionalPlan(bool sliceBatch, bool confirm)
    {
        const string direction = "Implement data model then service layer.\r\nPreserve café input.";
        var path = Path.Combine(CreateTempDirectory(), "direction brief.md");
        File.WriteAllText(path, direction, new System.Text.UTF8Encoding(false));
        string[] flags = [.. sliceBatch ? new[] { "--slice-batch" } : Array.Empty<string>(),
            .. confirm ? new[] { "--confirm-plan" } : Array.Empty<string>()];
        var baseline = RunPlan(["plan", direction, .. flags], sliceBatch);
        AssertPlannerDirection(baseline.prompt, direction, sliceBatch);

        foreach (var fileFlag in new[] { "--text-file", "--brief-file" })
        {
            foreach (var flagsFirst in new[] { false, true })
            {
                string[] parts = flagsFirst
                    ? ["plan", .. flags, fileFlag, path]
                    : ["plan", fileFlag, path, .. flags];
                var actual = RunPlan(parts, sliceBatch);
                AssertPlannerDirection(actual.prompt, direction, sliceBatch);
                Assert.Equal(PlanPreview(baseline.output), PlanPreview(actual.output));
                Assert.Equal(GoalObjectives(baseline.kernel), GoalObjectives(actual.kernel));
                Assert.Contains($"for direction: {direction}", actual.output);
                if (!confirm)
                {
                    Assert.Empty(actual.kernel.Goals);
                }
                else if (sliceBatch)
                {
                    Assert.Equal(4, actual.kernel.Goals.Count);
                    Assert.Equal(direction, actual.kernel.Goals.Single(g => g.SliceBatchParentId is null).Objective);
                }
                else
                {
                    Assert.Equal(2, actual.kernel.Goals.Count);
                    var model = actual.kernel.Goals.Single(g => g.Objective == "Implement the data model");
                    var service = actual.kernel.Goals.Single(g => g.Objective == "Implement the service layer");
                    Assert.Equal(model.Id, Assert.Single(service.DependsOn));
                }
            }
        }
    }

    [Xunit.Fact]
    public void StandardInput_Redirected_PreservesDirection()
    {
        const string direction = "Implement three independent feature slices.\r\nKeep café and trailing spaces.  ";
        using var input = new StringReader(direction);
        var result = RunPlan(["plan", "--text-file", "-", "--slice-batch"], true, input, true);

        AssertPlannerDirection(result.prompt, direction, true);
        Assert.Contains($"for direction: {direction}", result.output);
        Assert.Empty(result.kernel.Goals);
    }

    [Xunit.Fact]
    public void StandardInput_NotRedirected_RejectsWithoutReading()
    {
        using var input = new FailOnReadTextReader();
        var exception = AssertPlanFails<InvalidOperationException>(
            ["plan", "--text-file", "-", "--slice-batch", "--confirm-plan"], input, false);

        Assert.Equal("Standard input is not redirected; pipe content or provide a file.", exception.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("--text-file")]
    [Xunit.InlineData("--brief-file")]
    public void InlineAndFile_RejectsBeforeCreatingGoals(string fileFlag)
    {
        var exception = AssertPlanFails<ArgumentException>(
            ["plan", "Implement feature slices", fileFlag, "unused.md", "--slice-batch", "--confirm-plan"]);

        Assert.Equal($"Provide either inline text or {fileFlag} <path>, not both.", exception.Message);
    }

    [Xunit.Fact]
    public void TwoFileFlags_RejectsBeforeCreatingGoals()
    {
        var exception = AssertPlanFails<ArgumentException>(
            ["plan", "--text-file", "unused.md", "--brief-file", "unused.md", "--confirm-plan"]);

        Assert.Equal("Provide only one text file option: --brief-file, --text-file.", exception.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("--text-file")]
    [Xunit.InlineData("--brief-file")]
    public void MissingFile_RejectsBeforeCreatingGoals(string fileFlag)
    {
        var path = Path.Combine(CreateTempDirectory(), "missing.md");
        var exception = AssertPlanFails<InvalidOperationException>(
            ["plan", fileFlag, path, "--slice-batch", "--confirm-plan"]);

        Assert.Equal($"{fileFlag} not found: {path}", exception.Message);
    }

    [Xunit.Fact]
    public void LongFileDirection_ReachesPlannerWithoutTruncation()
    {
        var direction = LongDirection();
        var path = Path.Combine(CreateTempDirectory(), "long brief.md");
        File.WriteAllText(path, direction, new System.Text.UTF8Encoding(false));
        string[] parts = ["plan", "--text-file", path, "--slice-batch"];
        var plannerInput = BuildPlanInput(parts);
        var result = RunPlan(parts, true);

        Assert.True(direction.Length > 8191);
        Assert.Equal(direction, plannerInput.Direction);
        Assert.Equal(GoalDagDecompositionPlanner.BuildPrompt(direction, true), plannerInput.Prompt);
        AssertPlannerDirection(plannerInput.Prompt, direction, true);
        Assert.Contains($"for direction: {direction}", result.output);
        Assert.Contains("Dormant slice-batch intake preview", result.output);
        Assert.Empty(result.kernel.Goals);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Plan_LongFileDirection_DeliversWholeBlocksToAllSamples(bool sliceBatch)
    {
        var direction = LongDirection() + "\nScope: preserve the middle constraints.\nTail: Ω";
        Assert.True(direction.Length > 10_000);
        var path = Path.Combine(CreateTempDirectory(), "full direction.md");
        File.WriteAllText(path, direction, new System.Text.UTF8Encoding(false));
        string[] parts = sliceBatch
            ? ["plan", "--text-file", path, "--slice-batch"]
            : ["plan", "--text-file", path];

        var result = RunPlan(parts, sliceBatch);

        Assert.Equal(3, result.requests.Count);
        Assert.All(result.requests, request =>
        {
            var userMessage = Assert.Single(request.Messages, message => message.Role == "user");
            Assert.StartsWith(
                $"Goal: {direction}{Environment.NewLine}" +
                $"Task: {GoalDagDecompositionPlanner.BuildPrompt(direction, sliceBatch)}{Environment.NewLine}" +
                "Task role: Planner", userMessage.Content);
            AssertPlannerDirection(userMessage.Content, direction, sliceBatch);
            Assert.DoesNotContain("[truncated", userMessage.Content);
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Runner_DefaultOption_PreservesExistingPrimaryContextLimits(bool complex)
    {
        var objective = "Goal start: " + LongDirection() + ": goal end";
        var description = (complex
            ? "Design and implement full implementation with architecture and integration: "
            : "Report the existing labels: ") + LongDirection() + ": task end";

        async Task<(string Prompt, TaskComplexity Complexity)> Run(bool explicitDefault)
        {
            var provider = new RecordingProvider("OK");
            var (kernel, _, agents, providers) = BuildTestContext("OK", provider);
            var task = new TaskSpec(TaskId.New(), description, AgentRole.Planner, "Return the report.");
            var goal = kernel.CreateGoal(objective, [task]);
            kernel.ActivateGoal(goal.Id, agents);
            var runner = explicitDefault
                ? new AgentTaskRunner(kernel, agents, providers, preservePrimaryContext: false)
                : new AgentTaskRunner(kernel, agents, providers);

            await runner.RunAsync(goal.Id, task.Id);

            var request = Assert.Single(provider.Requests);
            var userMessage = Assert.Single(request.Messages, message => message.Role == "user");
            Assert.NotNull(task.LastExecution);
            Assert.NotNull(task.LastExecution.TaskComplexity);
            return (userMessage.Content, task.LastExecution.TaskComplexity.Value);
        }

        var defaultRun = await Run(explicitDefault: false);
        var explicitRun = await Run(explicitDefault: true);
        Assert.Equal(complex ? TaskComplexity.Complex : TaskComplexity.Simple, defaultRun.Complexity);
        Assert.Equal(defaultRun.Complexity, explicitRun.Complexity);
        Assert.Equal(defaultRun.Prompt, explicitRun.Prompt);
        var (head, tail) = defaultRun.Complexity == TaskComplexity.Complex ? (1600, 800) : (800, 400);
        string ExpectedBlock(string text) => text[..head] +
            $"{Environment.NewLine}...[truncated {text.Length - head - tail} chars for prompt budget]...{Environment.NewLine}" +
            text[^tail..];
        Assert.StartsWith(
            $"Goal: {ExpectedBlock(objective)}{Environment.NewLine}" +
            $"Task: {ExpectedBlock(description)}{Environment.NewLine}" +
            "Task role: Planner", defaultRun.Prompt);
        Assert.DoesNotContain(objective, defaultRun.Prompt);
        Assert.DoesNotContain(description, defaultRun.Prompt);
    }

    [Xunit.Fact]
    public void LongPositionalDirection_PreservesExistingPreview()
    {
        var direction = LongDirection();
        string[] parts = ["plan", direction, "--slice-batch"];
        var plannerInput = BuildPlanInput(parts);
        var result = RunPlan(parts, true);

        Assert.Equal(direction, plannerInput.Direction);
        Assert.Equal(GoalDagDecompositionPlanner.BuildPrompt(direction, true), plannerInput.Prompt);
        AssertPlannerDirection(plannerInput.Prompt, direction, true);
        Assert.Contains($"for direction: {direction}", result.output);
        Assert.Contains("Dormant slice-batch intake preview", result.output);
        Assert.Empty(result.kernel.Goals);
    }

    [Xunit.Fact]
    public void MissingDirection_UsageShowsFileForm()
    {
        var exception = AssertPlanFails<ArgumentException>(["plan"]);

        Assert.Contains("plan <direction>", exception.Message);
        Assert.Contains("plan --text-file <path|->", exception.Message);
        Assert.Contains("--brief-file is an alias", exception.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Preview_ConfirmHintShowsFileAndPositionalForms(bool sliceBatch)
    {
        string[] parts = sliceBatch
            ? ["plan", "Implement feature slices", "--slice-batch"]
            : ["plan", "Implement model then service"];
        var result = RunPlan(parts, sliceBatch);
        var flags = sliceBatch ? "--slice-batch --confirm-plan" : "--confirm-plan";

        Assert.Contains($"plan <direction> {flags}", result.output);
        Assert.Contains($"plan --text-file <path> {flags}", result.output);
    }

    [Xunit.Theory]
    [Xunit.InlineData("plan --text-file brief.md --slice-batch --confirm-plan")]
    [Xunit.InlineData("plan --slice-batch --confirm-plan --text-file brief.md")]
    [Xunit.InlineData("plan --brief-file - --confirm-plan --slice-batch")]
    public void FileFlags_ParsingPreservesValuesAndFlagOrder(string command)
    {
        var expected = command.Split(' ');

        Assert.Equal(expected, CliArgumentParser.NormalizeArgs(expected).ToArray());
        Assert.Equal(expected, CliArgumentParser.SplitCommand(command).ToArray());
    }

    private static string LongDirection() => string.Concat(
        Enumerable.Range(0, 500).Select(index => $"{index:D4}:abcdefghijklmno"));

    private static (string Direction, string Prompt) BuildPlanInput(string[] parts)
    {
        var (kernel, workspace, agents, providers) = BuildSliceBatchTestContext(ThreeSliceBatchJson);
        var context = new CliExecutionContext(
            kernel, workspace, providers, agents, WorkerProfileCatalog.Default(), currentGoal: null);
        return CliCommandHandlers.BuildPlanDecompositionInput(
            context, CliArgumentParser.NormalizeArgs(parts));
    }

    private static string[] GoalObjectives(AgentOrchestratorKernel kernel) =>
        kernel.Goals.Select(g => g.Objective).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static string PlanPreview(string output)
    {
        var end = output.IndexOf("Created ", StringComparison.Ordinal);
        return end < 0 ? output : output[..end];
    }

    private static void AssertPlannerDirection(string prompt, string direction, bool sliceBatch)
    {
        Assert.Contains(GoalDagDecompositionPlanner.BuildPrompt(direction, sliceBatch), prompt);
        const string marker = "Direction: ";
        var markerIndex = prompt.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "Planner request must contain the direction marker.");
        var start = markerIndex + marker.Length;
        var end = prompt.IndexOf("\nExample:", start, StringComparison.Ordinal);
        Assert.True(end >= start, "Planner request must contain the example after the direction.");
        if (prompt[end - 1] == '\r')
            end--;
        Assert.Equal(direction, prompt[start..end]);
    }

    private static (AgentOrchestratorKernel kernel, string output, string prompt, IReadOnlyList<ModelRequest> requests) RunPlan(
        string[] parts, bool sliceBatch, TextReader? standardInput = null, bool? redirected = null)
    {
        var provider = new RecordingProvider(sliceBatch ? ThreeSliceBatchJson : TwoNodeJson);
        var (kernel, workspace, agents, providers) = sliceBatch
            ? BuildSliceBatchTestContext(ThreeSliceBatchJson, provider)
            : BuildTestContext(TwoNodeJson, provider);
        using var samples = PlanSampleProviderBridge.Use(providers);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            parts, kernel, workspace, ref agents, providers, ref profiles, ref currentGoal,
            standardInput: standardInput, isStandardInputRedirected: redirected));
        var requests = provider.Requests.ToArray();
        Assert.NotEmpty(requests);
        var prompt = string.Join('\n', requests[^1].Messages.Select(message => message.Content));
        return (kernel, output, prompt, requests);
    }

    private static T AssertPlanFails<T>(
        string[] parts, TextReader? standardInput = null, bool? redirected = null) where T : Exception
    {
        var provider = new FakeSmokeProvider(ThreeSliceBatchJson, providerName: "Fake");
        var (kernel, workspace, agents, providers) = BuildSliceBatchTestContext(ThreeSliceBatchJson, provider);
        using var samples = PlanSampleProviderBridge.Use(providers);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var exception = Assert.Throws<T>(() => CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            parts, kernel, workspace, ref agents, providers, ref profiles, ref currentGoal,
            standardInput: standardInput, isStandardInputRedirected: redirected)));
        Assert.Empty(kernel.Goals);
        Assert.Null(provider.LastRequest);
        return exception;
    }

    private sealed class FailOnReadTextReader : TextReader
    {
        public override string ReadToEnd() => throw new InvalidOperationException("Unexpected stdin read.");
    }

    private const string TwoNodeJson = """
        ```json
        [{"id":"g1","objective":"Implement the data model","dependsOn":[]},{"id":"g2","objective":"Implement the service layer","dependsOn":["g1"]}]
        ```
        """;

    private const string ThreeSliceBatchJson = """
        ```json
        [{"id":"g1","objective":"Implement feature A.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureA/A.cs","dependsOn":[]},{"id":"g2","objective":"Implement feature B.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureB/B.cs","dependsOn":[]},{"id":"g3","objective":"Implement feature C.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureC/C.cs","dependsOn":[]}]
        ```
        """;

    private static (InvalidOperationException exception, AgentOrchestratorKernel kernel) ConfirmInvalidSliceBatch(string plannerOutput)
    {
        var (kernel, workspace, agents, providers) = BuildSliceBatchTestContext(plannerOutput);
        using var samples = PlanSampleProviderBridge.Use(providers);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["plan", "Implement file-disjoint slices", "--slice-batch", "--confirm-plan"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));
        return (exception, kernel);
    }

    private static string SliceBatchJson(params string[] nodes) =>
        $"```json\n[{string.Join(',', nodes)}]\n```";

    private static string SliceObjective(string id, string path, string? dependency = null)
    {
        var dependencies = dependency is null ? "[]" : $"[\"{dependency}\"]";
        return $"{{\"id\":\"{id}\",\"objective\":\"Implement {id}.\\n\\nTarget files/scopes:\\nScope confidence: precise\\nIncludes:\\n- {path}\",\"dependsOn\":{dependencies}}}";
    }

    private static (AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents, IModelProviderRegistry providers)
        BuildTestContext(string plannerOutput, IModelProvider? plannerProvider = null)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();

        var plannerAgent = new AgentDefinition(
            AgentId.New(),
            "Test-Planner",
            AgentRole.Planner,
            new ModelProfile("Fake", "fake-plan-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);

        var developerAgent = new AgentDefinition(
            AgentId.New(),
            "Test-Developer",
            AgentRole.Developer,
            new ModelProfile("Fake", "fake-dev-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);

        IReadOnlyList<AgentDefinition> agents = [plannerAgent, developerAgent];
        var provider = plannerProvider ?? new FakeSmokeProvider(plannerOutput, providerName: "Fake");
        IModelProviderRegistry providers = new InMemoryModelProviderRegistry([provider]);

        return (kernel, workspace, agents, providers);
    }

    private static (AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents, IModelProviderRegistry providers)
        BuildSliceBatchTestContext(string plannerOutput, IModelProvider? plannerProvider = null)
    {
        var context = BuildTestContext(plannerOutput, plannerProvider);
        ModelFunctionCatalogStore.Save(context.workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        return context;
    }

    private sealed class RecordingProvider(string text) : IModelProvider
    {
        private readonly FakeSmokeProvider _inner = new(text, providerName: "Fake");

        public string ProviderName => _inner.ProviderName;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return _inner.CompleteAsync(request, cancellationToken);
        }
    }
}
