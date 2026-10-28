using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private trees, kernels and SQLite stores; process launch is injected and forbidden.
public sealed class BriefPlannedTestLaneReadinessTests
{
    private const string Name = "BuildWidgetTests";
    private const string Manifest = """
        { "version": 1, "engine": { "infrastructureTestLanes": [
          { "name": "Build lane", "filter": "FullyQualifiedName~Build", "ownedCollections": ["BuildCollection"] },
          { "name": "Remainder", "filter": "FullyQualifiedName!~Build" }
        ] } }
        """;
    private const string Plan = "Plan lane 'Build lane': change filter in config/acceptance-manifest.json " +
        "to FullyQualifiedName~BuildWidgetTests and update the pinned membership test.";

    [Theory]
    [InlineData(false, "profile")]
    [InlineData(true, "profile")]
    [InlineData(false, "subscription")]
    [InlineData(true, "subscription")]
    public void NoTestDeclarations_MissingManifestDoesNotPreventDeveloperPreparation(bool projectScoped, string route)
    {
        using var fixture = new Fixture("Inspect docs/project.md and summarize.");
        File.Delete(fixture.ManifestPath);
        var projectHome = projectScoped ? Path.Combine(fixture.Root, "project-home") : null;
        if (projectHome is not null) Directory.CreateDirectory(projectHome);
        var workspace = projectHome is null ? fixture.Workspace
            : fixture.Workspace with { IsProjectScoped = true, OrchestratorDirectory = projectHome };
        Assert.False(File.Exists(AcceptanceManifestLocator.Resolve(fixture.Root, projectHome)));
        var report = fixture.Readiness(projectHome);
        Assert.True(report.AllowsStart(false));
        Assert.DoesNotContain(report.Findings,
            item => item.Kind is BriefPlannedTestLaneReadiness.ConfigurationKind or LaneSubstringCollisionCheck.Rule);

        var preparationCalls = 0;
        var operations = new GoalDispatchOperations(resolveHome: _ =>
        {
            preparationCalls++;
            throw new InvalidOperationException("Admitted to preparation.");
        });
        var failure = Assert.Throws<InvalidOperationException>(() =>
        {
            if (route == "subscription")
                operations.SubscriptionDispatchTask(fixture.Kernel, workspace, fixture.Goal,
                    fixture.Developer, fixture.Agents, new WorkerProfileCatalog([fixture.Profile]));
            else
                operations.ProfileDispatchTask(fixture.Kernel, workspace, fixture.Goal,
                    fixture.Developer, fixture.Profile);
        });
        Assert.Equal(1, preparationCalls);
        Assert.Equal("Admitted to preparation.", failure.Message);
        Assert.Null(fixture.Developer.LastDispatch);
        Assert.Null(fixture.Developer.LastProcess);
    }

    [Theory]
    [InlineData("new test class BuildWidgetTests")]
    [InlineData("new test class Example.BuildWidgetTests")]
    [InlineData("new test class Widget.Historical.BuildWidgetTests")]
    [InlineData("new test class Existing+BuildWidgetTests")]
    [InlineData("new tests/Widget.Tests/BuildWidgetTests.cs")]
    [InlineData("new tests/Example/BuildWidgetTests.cs")]
    [InlineData(@"new tests\Example\BuildWidgetTests.cs")]
    [InlineData("new tests/example-tests/BuildWidgetTests.cs")]
    [InlineData("`tests/Widget.Tests/BuildWidgetTests.cs` (new file)")]
    public void ExplicitCollision_ReadinessBlocksBothConfirmationStates(string declaration)
    {
        using var fixture = new Fixture(declaration);
        var before = File.ReadAllBytes(fixture.ManifestPath);
        var report = fixture.Readiness();

        var finding = Assert.Single(report.Findings, item => item.Kind == LaneSubstringCollisionCheck.Rule);
        Assert.Equal(GoalReadinessSeverity.Blocker, finding.Severity);
        Assert.False(finding.CanOverride);
        Assert.Contains(Name, finding.Message);
        Assert.Contains("Build lane", finding.Message);
        Assert.Contains("FullyQualifiedName~Build", finding.Message);
        Assert.Contains("Rename", finding.Message);
        Assert.Contains("manifest/filter and pinned membership test", finding.Message);
        Assert.True(report.HasHardBlockers);
        Assert.False(report.AllowsStart(false));
        Assert.False(report.AllowsStart(true));
        Assert.Throws<InvalidOperationException>(() => GoalReadinessPreflight.ThrowIfStartBlocked(report, true));
        Assert.Equal(before, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(File.Exists(fixture.ProposedPath));
    }

    [Theory]
    [InlineData("profile", Name)]
    [InlineData("subscription", Name)]
    [InlineData("batch", Name)]
    [InlineData("start-ready", Name)]
    [InlineData("profile", "Example.BuildWidgetTests")]
    [InlineData("subscription", "Example.BuildWidgetTests")]
    [InlineData("batch", "Example.BuildWidgetTests")]
    [InlineData("start-ready", "Example.BuildWidgetTests")]
    public void UnresolvedCollision_ApplicationRefusesDeveloperPreparation(string route, string className)
    {
        using var fixture = new Fixture("new test class " + className);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Developer.Status);
        Assert.Empty(DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(fixture.Goal, fixture.Developer));
        var homeCalls = 0;
        var operations = new GoalDispatchOperations(resolveHome: _ =>
        {
            homeCalls++;
            throw new InvalidOperationException("Preparation reached home resolution.");
        });
        var launchCalls = 0;
        var runner = new BackgroundDispatchRunner(startProcess: _ =>
        {
            launchCalls++;
            throw new InvalidOperationException("Unexpected worker launch.");
        });
        var profiles = new WorkerProfileCatalog([fixture.Profile]);
        var failure = Assert.Throws<InvalidOperationException>(() =>
        {
            switch (route)
            {
                case "profile":
                    operations.ProfileDispatchTask(fixture.Kernel, fixture.Workspace, fixture.Goal,
                        fixture.Developer, fixture.Profile);
                    break;
                case "subscription":
                    operations.SubscriptionDispatchTask(fixture.Kernel, fixture.Workspace, fixture.Goal,
                        fixture.Developer, fixture.Agents, profiles);
                    break;
                case "batch":
                    operations.SubscriptionDispatchReadyBatch(fixture.Kernel, fixture.Workspace, fixture.Goal,
                        fixture.Agents, profiles);
                    break;
                case "start-ready":
                    operations.StartSubscriptionReadyTasks(fixture.Kernel, fixture.Workspace, fixture.Goal,
                        fixture.Agents, profiles, runner: runner);
                    break;
                default: throw new ArgumentException(route);
            }
        });

        Assert.Contains(LaneSubstringCollisionCheck.Rule, failure.Message);
        Assert.Contains(className, failure.Message);
        Assert.Equal(0, homeCalls);
        Assert.Equal(0, launchCalls);
        Assert.Null(fixture.Developer.LastDispatch);
        Assert.Null(fixture.Developer.LastProcess);
        Assert.False(File.Exists(fixture.ProposedPath));
    }

    [Theory]
    [InlineData("existing-purpose")]
    [InlineData("existing-prefix")]
    [InlineData("existing-prefix-path")]
    [InlineData("historical-prefix")]
    [InlineData("separate-collection")]
    [InlineData("separate-path-collection")]
    [InlineData("negated-suffix")]
    [InlineData("negated-path-suffix")]
    [InlineData("negated-prefix")]
    [InlineData("negated-filter-plan")]
    [InlineData("production-update-prefix")]
    [InlineData("production-update-path-prefix")]
    [InlineData("production-fix-prefix")]
    [InlineData("production-refactor-path-prefix")]
    [InlineData("production-reenable-prefix")]
    [InlineData("production-reenable-path-prefix")]
    [InlineData("short-reenable-prefix")]
    [InlineData("short-reenable-path-prefix")]
    [InlineData("negated-membership-work")]
    [InlineData("never-membership-work")]
    [InlineData("must-not-membership-work")]
    [InlineData("will-not-membership-work")]
    [InlineData("dont-membership-work")]
    [InlineData("membership-mention-only")]
    [InlineData("does-not-membership-work")]
    [InlineData("does-not-membership-work-conjunction")]
    public void DeclarationContext_UnownedClassBlocksDeveloperPreparation(string kind)
    {
        var scope = kind switch
        {
            "existing-purpose" => "Add new test class BuildWidgetTests to cover existing widget behavior.",
            "existing-prefix" => "To cover existing widget behavior, add new test class BuildWidgetTests.",
            "existing-prefix-path" => "To cover existing widget behavior, add new tests/Widget.Tests/BuildWidgetTests.cs.",
            "historical-prefix" => "To cover historical widget inputs, add new test class BuildWidgetTests.",
            "separate-collection" => "new test class BuildWidgetTests. Add new test class QuokkaTests in collection 'BuildCollection'.",
            "separate-path-collection" => "new tests/Widget.Tests/BuildWidgetTests.cs. Add new test class QuokkaTests in collection 'BuildCollection'.",
            "negated-suffix" => "Add new test class BuildWidgetTests; do not edit the manifest.",
            "negated-path-suffix" => "Add new tests/Widget.Tests/BuildWidgetTests.cs; do not edit the manifest.",
            "negated-prefix" => "Do not edit the manifest; add new test class BuildWidgetTests.",
            "negated-filter-plan" => "Add new test class BuildWidgetTests; " + Plan.Replace("change filter", "do not change filter"),
            "production-update-prefix" => "Update production widget handling; add new test class BuildWidgetTests.",
            "production-update-path-prefix" => "Update production widget handling; add new tests/Widget.Tests/BuildWidgetTests.cs.",
            "production-fix-prefix" => "Fix production widget handling; add new test class BuildWidgetTests.",
            "production-refactor-path-prefix" => "Refactor production widget handling; add new tests/Widget.Tests/BuildWidgetTests.cs.",
            "production-reenable-prefix" => "Re-enable production widget handling; add new test class BuildWidgetTests.",
            "production-reenable-path-prefix" => "Re-enable production widget handling; add new tests/Widget.Tests/BuildWidgetTests.cs.",
            "short-reenable-prefix" => "Re-enable widgets; add new test class BuildWidgetTests.",
            "short-reenable-path-prefix" => "Re-enable widgets; add new tests/Widget.Tests/BuildWidgetTests.cs.",
            "negated-membership-work" => "Add new test class BuildWidgetTests; " + Plan.Replace("update the pinned", "do not update the pinned"),
            "never-membership-work" => "Add new test class BuildWidgetTests; " + Plan.Replace("update the pinned", "never update the pinned"),
            "must-not-membership-work" => "Add new test class BuildWidgetTests; " + Plan.Replace("update the pinned", "must not update the pinned"),
            "will-not-membership-work" => "Add new test class BuildWidgetTests; " + Plan.Replace("update the pinned", "will not update the pinned"),
            "dont-membership-work" => "Add new test class BuildWidgetTests; " + Plan.Replace("update the pinned", "don't update the pinned"),
            "membership-mention-only" => "Add new test class BuildWidgetTests; " + Plan.Replace("update the pinned", "mention the pinned"),
            "does-not-membership-work" => "Add new test class BuildWidgetTests; " +
                Plan.Replace("and update the pinned", "; this change does not update the pinned"),
            "does-not-membership-work-conjunction" => "Add new test class BuildWidgetTests; " +
                Plan.Replace("and update the pinned", "and this change does not update the pinned"),
            _ => throw new ArgumentException(kind)
        };
        using var fixture = new Fixture(scope);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Developer.Status);
        Assert.Empty(DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(fixture.Goal, fixture.Developer));
        var before = File.ReadAllBytes(fixture.ManifestPath);
        var report = fixture.Readiness();
        var finding = Assert.Single(report.Findings, item => item.Kind == LaneSubstringCollisionCheck.Rule);
        Assert.Contains(Name, finding.Message);
        Assert.DoesNotContain("QuokkaTests", finding.Message);
        Assert.False(report.AllowsStart(true));
        Assert.DoesNotContain(report.Findings,
            item => item.Kind is BriefPlannedTestLaneReadiness.PendingKind or BriefPlannedTestLaneReadiness.ConfigurationKind);
        Assert.Equal(before, File.ReadAllBytes(fixture.ManifestPath));
        var preparationCalls = 0;
        var operations = new GoalDispatchOperations(resolveHome: _ =>
        {
            preparationCalls++;
            throw new InvalidOperationException("Unexpected preparation.");
        });

        var failure = Assert.Throws<InvalidOperationException>(() => operations.ProfileDispatchTask(
            fixture.Kernel, fixture.Workspace, fixture.Goal, fixture.Developer, fixture.Profile));

        Assert.Contains(LaneSubstringCollisionCheck.Rule, failure.Message);
        Assert.Contains(Name, failure.Message);
        Assert.Equal(0, preparationCalls);
        Assert.Null(fixture.Developer.LastDispatch);
        Assert.Null(fixture.Developer.LastProcess);
        Assert.False(File.Exists(fixture.ProposedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedCollision_ApplicationRefusesLaunchBeforeRefresh(bool refresh)
    {
        using var fixture = new Fixture("new test class " + Name);
        fixture.Kernel.RecordTaskDispatch(fixture.Goal.Id, fixture.Developer.Id,
            new TaskDispatchRecord("fixture", "worker", fixture.Root, DateTimeOffset.UtcNow));
        Assert.Contains(fixture.Kernel.BuildProcessBatchPlan(fixture.Goal.Id, ProcessBatchActionKind.StartDispatches).Items,
            item => item.TaskId == fixture.Developer.Id && item.Status == ProcessBatchItemStatus.Ready);
        var launchCalls = 0;
        var runner = new BackgroundDispatchRunner(startProcess: _ =>
        {
            launchCalls++;
            throw new InvalidOperationException("Unexpected worker launch.");
        });
        var result = new GoalDispatchOperations().StartDispatches(fixture.Kernel, fixture.Workspace,
            fixture.Goal, refreshBeforeStart: refresh, runner: runner);

        Assert.Contains(LaneSubstringCollisionCheck.Rule, Assert.Single(result.StartRefusals!).Reason);
        Assert.Empty(result.Tasks);
        Assert.Empty(result.StartFailures!);
        Assert.Equal(0, launchCalls);
        Assert.Null(fixture.Developer.LastProcess);
        Assert.False(File.Exists(fixture.ProposedPath));
    }

    [Fact]
    public void ScoutSequence_PlannerCanPrepareWhileDeveloperRemainsBlocked()
    {
        using var fixture = new Fixture("new test class " + Name, scout: true);
        var planner = Assert.Single(fixture.Goal.Tasks, task => task.RequiredRole == AgentRole.Planner);
        var report = fixture.Readiness();
        Assert.True(report.AllowsStart(false));
        Assert.Equal(GoalReadinessSeverity.Warning,
            Assert.Single(report.Findings, item => item.Kind == LaneSubstringCollisionCheck.Rule).Severity);
        var operations = new GoalDispatchOperations(resolveHome: _ =>
            OrchestratorHome.ResolveForLaunch(VerifiedRepositoryRoot.Find(), _ => null));
        var prepared = operations.ProfileDispatchTask(fixture.Kernel, fixture.Workspace, fixture.Goal,
            planner, fixture.Profile, sandboxOptions: new WorkerSandboxOptions(false,
                WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));
        Assert.True(File.Exists(prepared.PromptPath));
        Assert.NotNull(planner.LastDispatch);
        fixture.Kernel.ReportTaskProgress(fixture.Goal.Id, planner.Id, WorkTaskStatus.Completed, "Planning finished.");
        Assert.False(fixture.Readiness().AllowsStart(true));
        var failure = Assert.Throws<InvalidOperationException>(() => operations.ProfileDispatchTask(
            fixture.Kernel, fixture.Workspace, fixture.Goal, fixture.Developer, fixture.Profile));
        Assert.Contains(LaneSubstringCollisionCheck.Rule, failure.Message);
        Assert.Null(fixture.Developer.LastDispatch);
    }

    [Theory]
    [InlineData("new test class QuokkaHarborTests")]
    [InlineData("new test class BuildWidgetTests in collection 'BuildCollection'")]
    [InlineData("new test class BuildWidgetTests with [Collection(\"BuildCollection\")]")]
    public void ConsistentPlacement_ReadinessAllowsStart(string declaration)
    {
        using var fixture = new Fixture(declaration);
        var report = fixture.Readiness();
        Assert.True(report.AllowsStart(false));
        Assert.DoesNotContain(report.Findings, finding => finding.Kind == LaneSubstringCollisionCheck.Rule);
    }

    [Theory]
    [InlineData("existing test class BuildWidgetTests")]
    [InlineData("Update tests/Widget.Tests/BuildWidgetTests.cs")]
    [InlineData("Example: new test class BuildWidgetTests")]
    [InlineData("Example: new test class Example.BuildWidgetTests")]
    [InlineData("Example. new test class Example.BuildWidgetTests")]
    [InlineData("Historical-evidence: new test class Example.BuildWidgetTests")]
    [InlineData("Historical evidence: new test class Example.BuildWidgetTests")]
    [InlineData("existing test class Example.BuildWidgetTests")]
    [InlineData("> new test class BuildWidgetTests")]
    [InlineData("\"new test class BuildWidgetTests\"")]
    [InlineData("Historical evidence: new test class BuildWidgetTests")]
    [InlineData("Regex: new test class BuildWidgetTests")]
    [InlineData("dotnet test --filter new test class BuildWidgetTests")]
    [InlineData("Run `dotnet test --filter new test class BuildWidgetTests`.")]
    [InlineData("Run `echo \"new test class BuildWidgetTests\"`.")]
    [InlineData("Run `Write-Output 'new test class BuildWidgetTests'`.")]
    [InlineData("Do not add new test class BuildWidgetTests")]
    [InlineData("No new test class BuildWidgetTests is required")]
    [InlineData("BuildWidgetTests is cited for reference")]
    [InlineData("```text\nnew test class BuildWidgetTests\n```")]
    public void IncidentalMentions_DoNotFabricateDeclarations(string text)
    {
        using var fixture = new Fixture(text);
        Assert.True(fixture.Readiness().AllowsStart(false));
        Assert.DoesNotContain(fixture.Readiness().Findings,
            finding => finding.Kind is LaneSubstringCollisionCheck.Rule or BriefPlannedTestLaneReadiness.PendingKind);
    }

    [Theory]
    [InlineData("double-quoted")]
    [InlineData("single-quoted")]
    [InlineData("quoted-action")]
    [InlineData("unquoted")]
    [InlineData("example-action")]
    [InlineData("comma-class")]
    [InlineData("comma-path")]
    [InlineData("semicolon-action")]
    [InlineData("execute-comma")]
    [InlineData("invoke-comma")]
    [InlineData("raw-comma-class")]
    [InlineData("raw-comma-path")]
    [InlineData("raw-comma-action")]
    [InlineData("raw-output-comma")]
    [InlineData("raw-custom-comma")]
    [InlineData("raw-action-command")]
    [InlineData("raw-semicolon-action")]
    [InlineData("raw-custom-semicolon-path")]
    [InlineData("raw-cmd-comma-class")]
    [InlineData("raw-cmd-comma-path")]
    [InlineData("raw-cmd-exe-comma-class")]
    [InlineData("raw-cmd-options-comma-class")]
    [InlineData("raw-cmd-options-comma-path")]
    [InlineData("raw-cmd-exe-options-comma-class")]
    [InlineData("raw-cmd-multiple-options-comma-path")]
    [InlineData("raw-custom-switches")]
    public void CommandContents_DoNotBlockDeveloperPreparation(string kind)
    {
        var scope = kind switch
        {
            "double-quoted" => "Run echo \"new test class BuildWidgetTests\".",
            "single-quoted" => "Run echo 'new test class BuildWidgetTests'.",
            "quoted-action" => "Run echo \"Add new test class BuildWidgetTests\".",
            "unquoted" => "Run echo new test class BuildWidgetTests.",
            "example-action" => "Example: cover existing widgets, add new test class BuildWidgetTests.",
            "comma-class" => "Run echo ignored, new test class BuildWidgetTests.",
            "comma-path" => "Run echo ignored, new tests/Widget.Tests/BuildWidgetTests.cs.",
            "semicolon-action" => "Run echo ignored; add new test class BuildWidgetTests.",
            "execute-comma" => "Execute echo ignored, new test class BuildWidgetTests.",
            "invoke-comma" => "Invoke echo ignored, new test class BuildWidgetTests.",
            "raw-comma-class" => "echo ignored, new test class BuildWidgetTests.",
            "raw-comma-path" => "echo ignored, new tests/Widget.Tests/BuildWidgetTests.cs.",
            "raw-comma-action" => "echo ignored, add new test class BuildWidgetTests.",
            "raw-output-comma" => "Write-Output ignored, new test class BuildWidgetTests.",
            "raw-custom-comma" => "custom-command ignored, new test class BuildWidgetTests.",
            "raw-action-command" => "Add-Content ignored, new test class BuildWidgetTests.",
            "raw-semicolon-action" => "echo ignored; add new test class BuildWidgetTests.",
            "raw-custom-semicolon-path" => "custom-command ignored; add new tests/Widget.Tests/BuildWidgetTests.cs.",
            "raw-cmd-comma-class" => "cmd /c echo ignored, add new test class BuildWidgetTests.",
            "raw-cmd-comma-path" => "cmd /c echo ignored, add new tests/Widget.Tests/BuildWidgetTests.cs.",
            "raw-cmd-exe-comma-class" => "cmd.exe /c echo ignored, add new test class BuildWidgetTests.",
            "raw-cmd-options-comma-class" => "cmd /d /c echo ignored, add new test class BuildWidgetTests.",
            "raw-cmd-options-comma-path" => "cmd /d /c echo ignored, add new tests/Widget.Tests/BuildWidgetTests.cs.",
            "raw-cmd-exe-options-comma-class" => "cmd.exe /d /c echo ignored, add new test class BuildWidgetTests.",
            "raw-cmd-multiple-options-comma-path" => "cmd /d /s /k echo ignored, add new tests/Widget.Tests/BuildWidgetTests.cs.",
            "raw-custom-switches" => "custom-command --message ignored text, add new test class BuildWidgetTests.",
            _ => throw new ArgumentException(kind)
        };
        using var fixture = new Fixture(scope);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Developer.Status);
        Assert.Empty(DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(fixture.Goal, fixture.Developer));
        var report = fixture.Readiness();
        Assert.True(report.AllowsStart(false));
        Assert.DoesNotContain(report.Findings,
            finding => finding.Kind is LaneSubstringCollisionCheck.Rule or BriefPlannedTestLaneReadiness.PendingKind);
        var preparationCalls = 0;
        var reachedPreparation = new InvalidOperationException("Preparation reached home resolution.");
        var operations = new GoalDispatchOperations(resolveHome: _ =>
        {
            preparationCalls++;
            throw reachedPreparation;
        });

        var failure = Assert.Throws<InvalidOperationException>(() => operations.ProfileDispatchTask(
            fixture.Kernel, fixture.Workspace, fixture.Goal, fixture.Developer, fixture.Profile));

        Assert.Same(reachedPreparation, failure);
        Assert.Equal(1, preparationCalls);
        Assert.Null(fixture.Developer.LastDispatch);
        Assert.Null(fixture.Developer.LastProcess);
        Assert.False(File.Exists(fixture.ProposedPath));
    }

    [Fact]
    public void ExistingSourceAndHistoricalSection_DoNotDeclareNewClasses()
    {
        using var fixture = new Fixture("new tests/Widget.Tests/BuildWidgetTests.cs\n" +
            "## Historical evidence\nnew test class BuildHistoryTests");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ProposedPath)!);
        File.WriteAllText(fixture.ProposedPath, "// An existing test file.");
        Assert.True(fixture.Readiness().AllowsStart(false));
    }

    [Fact]
    public void ConcretePlacementPlan_IsPendingAndDoesNotBypassCompletion()
    {
        using var fixture = new Fixture("new test class " + Name + ". " + Plan);
        var before = File.ReadAllBytes(fixture.ManifestPath);
        var report = fixture.Readiness();
        Assert.True(report.AllowsStart(false));
        var pending = Assert.Single(report.Findings, item => item.Kind == BriefPlannedTestLaneReadiness.PendingKind);
        Assert.Equal(GoalReadinessSeverity.Info, pending.Severity);
        Assert.Contains("owner-approved where required", pending.Message);
        Assert.Contains("completion and acceptance gates", pending.Message);
        var lanes = AcceptanceGateEngineSettings.Parse(Manifest).InfrastructureTestLanes;
        Assert.Single(LaneSubstringCollisionCheck.Find(lanes, lanes,
            [new AcceptanceTestClassDescriptor(Name, null)], "+public sealed " + "class " + Name));
        Assert.Equal(before, File.ReadAllBytes(fixture.ManifestPath));
    }

    [Theory]
    [InlineData("vague-manifest")]
    [InlineData("wrong-lane")]
    [InlineData("wrong-class")]
    [InlineData("missing-membership-test")]
    public void IncompletePlacementPlan_RemainsBlocking(string kind)
    {
        var plan = kind switch
        {
            "vague-manifest" => "Plan to update the manifest.",
            "wrong-lane" => "Plan lane 'Other lane': change filter in acceptance-manifest.json to FullyQualifiedName~BuildWidgetTests and update the pinned membership test.",
            "wrong-class" => "Plan lane 'Build lane': change filter in acceptance-manifest.json to FullyQualifiedName~UnrelatedTests and update the pinned membership test.",
            "missing-membership-test" => "Plan lane 'Build lane': change filter in acceptance-manifest.json to FullyQualifiedName~BuildWidgetTests.",
            _ => throw new ArgumentException(kind)
        };
        using var fixture = new Fixture("new test class " + Name + ". " + plan);
        Assert.False(fixture.Readiness().AllowsStart(true));
        Assert.Single(fixture.Readiness().Findings, item => item.Kind == LaneSubstringCollisionCheck.Rule);
    }

    [Theory]
    [InlineData("=")]
    [InlineData("!=")]
    [InlineData("trailing-wildcard")]
    [InlineData("invalid-tail")]
    public void UnsupportedSourceSelector_PlacementPlanRemainsBlocking(string selector)
    {
        var plan = selector switch
        {
            "trailing-wildcard" => Plan.Replace(Name, Name + "*"),
            "invalid-tail" => Plan.Replace(Name, Name + "/extra"),
            _ => Plan.Replace("FullyQualifiedName~", "FullyQualifiedName" + selector)
        };
        using var fixture = new Fixture("new test class " + Name + ". " + plan);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Developer.Status);
        Assert.Empty(DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(fixture.Goal, fixture.Developer));
        var before = File.ReadAllBytes(fixture.ManifestPath);
        var report = fixture.Readiness();

        Assert.False(report.AllowsStart(true));
        Assert.Single(report.Findings, item => item.Kind == LaneSubstringCollisionCheck.Rule);
        Assert.DoesNotContain(report.Findings,
            item => item.Kind is BriefPlannedTestLaneReadiness.PendingKind or BriefPlannedTestLaneReadiness.ConfigurationKind);
        Assert.Equal(before, File.ReadAllBytes(fixture.ManifestPath));
        var preparationCalls = 0;
        var operations = new GoalDispatchOperations(resolveHome: _ =>
        {
            preparationCalls++;
            throw new InvalidOperationException("Unexpected preparation.");
        });

        var failure = Assert.Throws<InvalidOperationException>(() => operations.ProfileDispatchTask(
            fixture.Kernel, fixture.Workspace, fixture.Goal, fixture.Developer, fixture.Profile));

        Assert.Contains(LaneSubstringCollisionCheck.Rule, failure.Message);
        Assert.Contains(Name, failure.Message);
        Assert.Equal(0, preparationCalls);
        Assert.Null(fixture.Developer.LastDispatch);
        Assert.Null(fixture.Developer.LastProcess);
        Assert.False(File.Exists(fixture.ProposedPath));
    }

    [Fact]
    public void MultipleSelectingLanes_OnePlanDoesNotHideOtherCollision()
    {
        using var fixture = new Fixture("new test class " + Name + ". " + Plan);
        fixture.WriteManifest(Manifest.Replace("{ \"name\": \"Remainder\"",
            "{ \"name\": \"Widget lane\", \"filter\": \"FullyQualifiedName~Widget\" }, { \"name\": \"Remainder\""));
        var report = fixture.Readiness();
        Assert.Single(report.Findings, item => item.Kind == BriefPlannedTestLaneReadiness.PendingKind);
        Assert.Contains("Widget lane", Assert.Single(report.Findings,
            item => item.Kind == LaneSubstringCollisionCheck.Rule).Message);
        Assert.False(report.AllowsStart(true));
    }

    [Fact]
    public void ConfiguredProjectManifest_WinsOverTargetAndHome()
    {
        using var fixture = new Fixture("new test class " + Name);
        var home = Path.Combine(fixture.Root, "install-home");
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "acceptance-manifest.json"), Manifest);
        var projectHome = Path.Combine(fixture.Root, "project-home");
        Directory.CreateDirectory(projectHome);
        var projectManifest = Manifest.Replace("FullyQualifiedName~Build", "FullyQualifiedName~Quokka")
            .Replace("FullyQualifiedName!~Build", "FullyQualifiedName!~Quokka");
        File.WriteAllText(Path.Combine(projectHome, "acceptance-manifest.json"), projectManifest);
        Assert.False(fixture.Readiness().AllowsStart(true));
        Assert.True(fixture.Readiness(projectHome).AllowsStart(false));
        // Target workspace and project context are the only inputs to the real application consumer.
        var workspace = fixture.Workspace with { IsProjectScoped = true, OrchestratorDirectory = projectHome };
        var reachedPreparation = false;
        var operations = new GoalDispatchOperations(resolveHome: _ =>
        {
            reachedPreparation = true;
            throw new InvalidOperationException("Admitted to preparation.");
        });
        var failure = Assert.Throws<InvalidOperationException>(() => operations.ProfileDispatchTask(
            fixture.Kernel, workspace, fixture.Goal, fixture.Developer, fixture.Profile));
        Assert.True(reachedPreparation);
        Assert.Equal("Admitted to preparation.", failure.Message);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("invalid-json", false)]
    [InlineData("invalid-filter", false)]
    [InlineData("duplicate-lane", false)]
    [InlineData("unreadable", false)]
    public void InvalidTargetConfiguration_BlocksWithoutHomeSubstitution(string kind, bool proposesTest)
    {
        using var fixture = new Fixture(proposesTest ? "new test class " + Name : "No proposed test declarations.");
        using var home = new Fixture("new test class " + Name);
        var projectHome = Path.Combine(fixture.Root, "project-home");
        Directory.CreateDirectory(projectHome);
        var manifestPath = Path.Combine(projectHome, "acceptance-manifest.json");
        File.Delete(fixture.ManifestPath);
        if (kind == "invalid-json") File.WriteAllText(manifestPath, "{ malformed");
        if (kind == "invalid-filter") File.WriteAllText(manifestPath,
            Manifest.Replace("FullyQualifiedName~Build", "UnsupportedFilter=Build"));
        if (kind == "duplicate-lane") File.WriteAllText(manifestPath,
            Manifest.Replace("\"name\": \"Remainder\"", "\"name\": \"Build lane\""));
        if (kind == "unreadable") File.WriteAllText(manifestPath, Manifest);
        using var locked = kind == "unreadable"
            ? new FileStream(manifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var report = fixture.Readiness(projectHome);
        Assert.False(report.AllowsStart(true));
        var failure = Assert.Single(report.Findings, item => item.Kind == BriefPlannedTestLaneReadiness.ConfigurationKind);
        Assert.Equal(GoalReadinessSeverity.Blocker, failure.Severity);
        Assert.False(failure.CanOverride);
        if (kind == "duplicate-lane") Assert.Contains("duplicated", failure.Message);
        Assert.Contains(projectHome, failure.Message);
        Assert.DoesNotContain(report.Findings, item => item.Kind == LaneSubstringCollisionCheck.Rule);
    }

    [Fact]
    public void ScoutConfigurationFailure_RemainsBlockingBeforePlanning()
    {
        using var fixture = new Fixture("new test class " + Name, scout: true);
        fixture.WriteManifest("{ malformed");
        var report = fixture.Readiness();
        Assert.False(report.AllowsStart(true));
        Assert.Equal(GoalReadinessSeverity.Blocker, Assert.Single(report.Findings,
            item => item.Kind == BriefPlannedTestLaneReadiness.ConfigurationKind).Severity);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal string ManifestPath => Path.Combine(Root, "config", "acceptance-manifest.json");
        internal string ProposedPath => Path.Combine(Root, "tests", "Widget.Tests", Name + ".cs");
        internal OrchestratorWorkspace Workspace { get; }
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; }
        internal TaskSpec Developer { get; } = new(TaskId.New(), "Implement the scoped change.", AgentRole.Developer);
        internal WorkerProfile Profile { get; } = new("fixture", "worker --cd {workingDirectory}");
        internal IReadOnlyList<AgentDefinition> Agents { get; }

        internal Fixture(string scope, bool scout = false)
        {
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Directory.CreateDirectory(Workspace.OrchestratorDirectory);
            StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            WriteManifest(Manifest);
            var planner = new TaskSpec(TaskId.New(), "Plan consistent placement.", AgentRole.Planner);
            Goal = Kernel.CreateGoal("Improve the widget.\n## Scope\n- " + scope,
                scout ? [planner, Developer] : [Developer]);
            Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec("Improve the widget.",
                ["Widget handles input."], VerificationClass.TestVerifiable, [], []));
            Agents = (scout ? new[] { AgentRole.Planner, AgentRole.Developer } : [AgentRole.Developer])
                .Select(role => new AgentDefinition(new AgentId(role.ToString()), role.ToString(), role,
                    new ModelProfile("OpenAI", AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias,
                        ModelCapability.Text, SubscriptionMode.ApiKey),
                    ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                    Subscription: new SubscriptionLaunchProfile("codex-cli",
                        AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, "low"))).ToArray();
            Kernel.ActivateGoal(Goal.Id, Agents);
        }

        internal void WriteManifest(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            File.WriteAllText(ManifestPath, text);
        }

        internal GoalReadinessPreflightReport Readiness(string? projectHome = null) =>
            GoalReadinessPreflight.Build(Goal, Agents, Root, resolveWorktree: (_, _) => Root,
                projectHomeDirectory: projectHome);

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
