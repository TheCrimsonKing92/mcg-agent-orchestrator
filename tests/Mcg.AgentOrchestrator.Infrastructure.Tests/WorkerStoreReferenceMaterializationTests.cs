using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class WorkerStoreReferenceMaterializationTests
{
    [Fact]
    public void WriteMaterializesMatchingGoalEventWithProvenance()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        const string matching = "{\"eventType\":\"criterion\",\"text\":\"feasibility-0d8fe45d51913385\"}";
        var source = fixture.WriteSource($"goal-events/{WorkerStoreReferenceFixture.EventGoalId}.jsonl",
            "{\"eventType\":\"other\"}\n" + matching + "\n{\"eventType\":\"after\"}\n");
        var objective = $"Read the recorded criterion.\nstore-ref: c1 = goal-events:{WorkerStoreReferenceFixture.EventGoalId}#contains=feasibility-0d8fe45d51913385";
        var (_, goal, task) = fixture.CreateGoal(objective);

        var context = new WorkerArtifactWriter().Write(goal, task, fixture.WorkingDirectory,
            orchestratorStoreRoot: fixture.StoreRoot, clock: fixture.Clock);

        var path = Path.Combine(context, "store-refs", "c1.md");
        Assert.True(File.Exists(path), "store-refs/c1.md must exist for a matching goal-events reference.");
        var content = File.ReadAllText(path);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        Assert.StartsWith("Store reference: c1" + Environment.NewLine, content);
        Assert.Contains("Kind: goal-events" + Environment.NewLine, content);
        Assert.Contains($"Locator: {WorkerStoreReferenceFixture.EventGoalId}" + Environment.NewLine, content);
        Assert.Contains("Selector: contains=feasibility-0d8fe45d51913385" + Environment.NewLine, content);
        Assert.Contains($"Source sha256: {hash}" + Environment.NewLine, content);
        Assert.Contains($"Resolved at: {fixture.Clock.UtcNow:O}" + Environment.NewLine, content);
        Assert.Equal(matching, WorkerStoreReferenceFixture.Excerpt(content));
        Assert.Equal(objective, goal.AuthoritativeBrief.Text);
        Assert.Contains(objective, File.ReadAllText(Path.Combine(context, "objective.md")));
        Assert.Contains("- store-refs/c1.md:", File.ReadAllText(Path.Combine(context, "manifest.md")));
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(context, "artifact-registry.json")));
        var entry = Assert.Single(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
            item => item.GetProperty("path").GetString() == "store-refs/c1.md");
        Assert.True(entry.GetProperty("hashVerified").GetBoolean());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            entry.GetProperty("sha256").GetString());
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(context, "context-package.json")));
        Assert.Equal("store-refs/c1.md", Assert.Single(package.RootElement.GetProperty("storeReferences").EnumerateArray()).GetString());
        Assert.Equal(content, File.ReadAllText(Path.Combine(context, "packages", task.Id.Value, "store-refs", "c1.md")));
    }

    [Fact]
    public void WriteReportsEachRefusedOrMissingReferenceWithoutMaterializingIt()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var context = fixture.WriteContext("Read selected records.\n"
            + "store-ref: forbidden = state-db:state.db\n"
            + "store-ref: traversal = operator-evidence:operator-evidence/../state.db\n"
            + "store-ref: absolute = trx:C:/outside/run.trx#test=Example\n"
            + "store-ref: missing = operator-evidence:operator-evidence/missing.md");

        Assert.False(Directory.Exists(Path.Combine(context, "store-refs")));
        var diagnostics = File.ReadAllLines(Path.Combine(context, "manifest.md"))
            .Where(line => line.StartsWith("STORE_REF_UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[]
        {
            "STORE_REF_UNRESOLVED name=forbidden reason=refused-kind",
            "STORE_REF_UNRESOLVED name=traversal reason=parent-traversal",
            "STORE_REF_UNRESOLVED name=absolute reason=absolute-path",
            "STORE_REF_UNRESOLVED name=missing reason=source-missing"
        }, diagnostics);
    }

    [Fact]
    public void WriteWithoutStoreRootReportsReferenceAndPreservesExistingArtifacts()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var (_, goal, task) = fixture.CreateGoal("Read evidence.\nstore-ref: c1 = operator-evidence:operator-evidence/c1.md");
        File.WriteAllText(Path.Combine(fixture.WorkingDirectory, "AGENTS.md"), "Fixture instructions.");
        var context = WorkerContextArtifacts.Write(goal, task, fixture.WorkingDirectory,
            preflightFindings: ["fixture-ready"], citedPriorEvidence: "Prior evidence.", clock: fixture.Clock);

        Assert.Contains("STORE_REF_UNRESOLVED name=c1 reason=store-root-unavailable",
            File.ReadAllText(Path.Combine(context, "manifest.md")));
        Assert.False(Directory.Exists(Path.Combine(context, "store-refs")));
        foreach (var file in new[]
        {
            "objective.md", "current-task.md", "digest.md", "manifest.md", "context-package.json", "artifact-registry.json",
            "deterministic-verification.md", "workflow-brokers.md", "context-budget.md", "selected-skills.md", "diff-summary.md",
            "prior-task-summaries.md", "prior-task-evidence.md", "prior-goal-evidence.md", "source-survey.md", "rule.md",
            "criteria-self-check.md", "subscription-preflight.md", "AGENTS.md"
        }) Assert.True(File.Exists(Path.Combine(context, file)), $"Pre-existing artifact must remain: {file}");
        Assert.Equal("*" + Environment.NewLine, File.ReadAllText(Path.Combine(fixture.WorkingDirectory, ".orchestrator-context", ".gitignore")));
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(context, "context-package.json")));
        Assert.False(package.RootElement.TryGetProperty("storeReferences", out _));
    }

    [Fact]
    public void WriteReportsMalformedInvalidAndDuplicateReferencesAndKeepsFirstDefinition()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource("operator-evidence/first.md", "first definition");
        fixture.WriteSource("operator-evidence/second.md", "second definition");
        var context = fixture.WriteContext("Read references.\n"
            + "store-ref: broken = no-kind\n"
            + "store-ref: ../unsafe = operator-evidence:operator-evidence/first.md\n"
            + "store-ref: chosen = operator-evidence:operator-evidence/first.md\n"
            + "store-ref: chosen = operator-evidence:operator-evidence/second.md");

        Assert.Equal("first definition", WorkerStoreReferenceFixture.Excerpt(File.ReadAllText(Path.Combine(context, "store-refs", "chosen.md"))));
        Assert.Single(Directory.GetFiles(Path.Combine(context, "store-refs")));
        Assert.Equal(new[]
        {
            "STORE_REF_UNRESOLVED name=broken reason=parse-error",
            "STORE_REF_UNRESOLVED name=invalid reason=invalid-name",
            "STORE_REF_UNRESOLVED name=chosen reason=duplicate-name"
        }, File.ReadAllLines(Path.Combine(context, "manifest.md"))
            .Where(line => line.StartsWith("STORE_REF_UNRESOLVED", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public void RefreshRemovesStaleFilesAndCurrentSnapshotButPreservesHistoricalSnapshot()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource("operator-evidence/c1.md", "record");
        var (_, goal, first) = fixture.CreateGoal("Read evidence.\nstore-ref: c1 = operator-evidence:operator-evidence/c1.md");
        var context = WorkerContextArtifacts.Write(goal, first, fixture.WorkingDirectory, orchestratorStoreRoot: fixture.StoreRoot, clock: fixture.Clock);
        var (_, noRefs, second) = fixture.CreateGoal("No references.");
        // Reuse the same goal/context identity while refreshing a different task snapshot.
        noRefs = new AgentOrchestratorKernel(fixture.Clock).CreateGoal(goal.Id, noRefs.Objective, [second]);
        WorkerContextArtifacts.Write(noRefs, second, fixture.WorkingDirectory, orchestratorStoreRoot: fixture.StoreRoot, clock: fixture.Clock);

        Assert.False(Directory.Exists(Path.Combine(context, "store-refs")));
        Assert.False(Directory.Exists(Path.Combine(context, "packages", second.Id.Value, "store-refs")));
        Assert.True(File.Exists(Path.Combine(context, "packages", first.Id.Value, "store-refs", "c1.md")));
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(context, "artifact-registry.json")));
        Assert.DoesNotContain(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
            item => item.GetProperty("path").GetString()!.StartsWith("store-refs/", StringComparison.Ordinal));
        // A refresh of the original task removes its stale snapshot as well.
        var sameTask = new AgentOrchestratorKernel(fixture.Clock).CreateGoal(goal.Id, "No references.", [first]);
        WorkerContextArtifacts.Write(sameTask, first, fixture.WorkingDirectory, orchestratorStoreRoot: fixture.StoreRoot, clock: fixture.Clock);
        Assert.False(Directory.Exists(Path.Combine(context, "packages", first.Id.Value, "store-refs")));
    }

    [Fact]
    public void DurablePlannerAndAnsweredEvidenceSourcesAreCollectedWithoutRewritingThem()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource("operator-evidence/plan.md", "plan record");
        fixture.WriteSource("operator-evidence/answer.md", "answer record");
        File.WriteAllText(Path.Combine(fixture.WorkingDirectory, "seed.txt"), "fixture");
        var kernel = new AgentOrchestratorKernel(fixture.Clock);
        var planner = new TaskSpec(TaskId.New(), "Plan behavior.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement behavior.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Read durable references.", [planner, developer]);
        var plan = WorkerDispatchTestSupport.PlannerContractPlanFixture()
            + "\nstore-ref: plan = operator-evidence:operator-evidence/plan.md";
        var outputPath = Path.Combine(fixture.WorkingDirectory, "planner.out.log");
        File.WriteAllText(outputPath, "Planner completed.");
        Assert.True(PlannerOutputContract.TryPersistDurableReceipt(outputPath, outputPath, plan, out var diagnostic), diagnostic);
        kernel.RecordTaskVerification(goal.Id, planner.Id,
            new TaskVerificationRecord("planner", fixture.WorkingDirectory, 0, "Planner completed.", "", fixture.Clock.UtcNow, StandardOutputPath: outputPath));
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Plan recorded.");
        const string answer = "store-ref: answer = operator-evidence:operator-evidence/answer.md";

        var context = WorkerContextArtifacts.Write(goal, developer, fixture.WorkingDirectory,
            orchestratorStoreRoot: fixture.StoreRoot, answeredEvidenceTexts: [answer], clock: fixture.Clock);

        Assert.Equal("plan record", WorkerStoreReferenceFixture.Excerpt(File.ReadAllText(Path.Combine(context, "store-refs", "plan.md"))));
        Assert.Equal("answer record", WorkerStoreReferenceFixture.Excerpt(File.ReadAllText(Path.Combine(context, "store-refs", "answer.md"))));
        Assert.Contains("store-ref: plan =", File.ReadAllText(Path.Combine(context, "planner-plan.md")));
        Assert.Equal("Read durable references.", goal.AuthoritativeBrief.Text);
    }
}
