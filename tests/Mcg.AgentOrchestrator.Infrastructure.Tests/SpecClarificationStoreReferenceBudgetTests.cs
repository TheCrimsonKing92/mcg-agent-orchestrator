using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all stores, captures and dispatch artifacts live under a unique fixture root.
public sealed class SpecClarificationStoreReferenceBudgetTests
{
    [Fact]
    public void BriefCollisionAndUnavailableLocators_KeepExistingResolverOutcomes()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var briefSource = fixture.WriteSource("operator-evidence/brief.md", "brief winner");
        fixture.WriteSource("operator-evidence/collision.md", "answer loser");
        var validSource = fixture.WriteSource("operator-evidence/valid.md", "answer evidence");
        var (kernel, goal, task) = fixture.CreateGoal("Implement scope.\n" + WorkerStoreReferenceFixture.Reference("collision", "brief"));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which evidence applies?");
        kernel.SubmitHumanInput(request.Id, string.Join("\n",
            WorkerStoreReferenceFixture.Reference("collision"), WorkerStoreReferenceFixture.Reference("valid"),
            WorkerStoreReferenceFixture.Reference("missing"), "store-ref: forbidden = state-db:state.db",
            "store-ref: traversal = operator-evidence:operator-evidence/../state.db"));

        var prepared = fixture.Prepare(kernel, goal, task);

        fixture.AssertDelivered(prepared, goal, "collision", briefSource, "brief winner");
        fixture.AssertDelivered(prepared, goal, "valid", validSource, "answer evidence");
        foreach (var name in new[] { "missing", "forbidden", "traversal" }) fixture.AssertAbsent(prepared, goal, name);
        Assert.Equal(new[]
        {
            "STORE_REF_UNRESOLVED name=collision reason=duplicate-name",
            "STORE_REF_UNRESOLVED name=missing reason=source-missing",
            "STORE_REF_UNRESOLVED name=forbidden reason=refused-kind",
            "STORE_REF_UNRESOLVED name=traversal reason=parent-traversal"
        }, fixture.Diagnostics(goal));
        var finalManifest = WorkerStoreReferenceFixture.PromptArtifact(prepared, "context/manifest.v1.json");
        foreach (var name in new[] { "missing", "forbidden", "traversal" })
            Assert.DoesNotContain($"context/store-refs/{name}.md", finalManifest);
        // Diagnostics retain the name/reason-only contract; raw locators are not appended.
        Assert.All(fixture.Diagnostics(goal), line => Assert.DoesNotContain("state.db", line));
    }

    [Fact]
    public void EarlierClarification_NearCountLimit_PreservesAllPrerequisiteSlots()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var (kernel, goal, task) = fixture.CreateGoal("Implement bounded evidence delivery.");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var clarification = kernel.RequestHumanInput(goal.Id, task.Id, "Clarification evidence?");
        foreach (var name in new[] { "clarification-first", "clarification-overflow" })
            fixture.WriteSource($"operator-evidence/{name}.md", name);
        kernel.SubmitHumanInput(clarification.Id, string.Join("\n",
            WorkerStoreReferenceFixture.Reference("clarification-first"), WorkerStoreReferenceFixture.Reference("clarification-overflow")));
        var names = Enumerable.Range(0, 15).Select(index => $"prerequisite-{index:00}").ToArray();
        foreach (var name in names) fixture.WriteSource($"operator-evidence/{name}.md", name);
        fixture.Advance();
        var first = kernel.RequestHumanInput(goal.Id, task.Id, "First prerequisite batch?", HumanWaitKind.PlannerPrerequisiteEvidence);
        kernel.SubmitHumanInput(first.Id, string.Join("\n", names.Take(7).Select(name => WorkerStoreReferenceFixture.Reference(name))));
        fixture.Advance();
        var second = kernel.RequestHumanInput(goal.Id, task.Id, "Second prerequisite batch?", HumanWaitKind.PlannerPrerequisiteEvidence);
        kernel.SubmitHumanInput(second.Id, string.Join("\n", names.Skip(7).Select(name => WorkerStoreReferenceFixture.Reference(name))));
        Assert.True(clarification.AnsweredAt < first.AnsweredAt);

        var prepared = fixture.Prepare(kernel, goal, task);

        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Context(goal), "context-package.json")));
        Assert.Equal(names.Append("clarification-first").Select(name => $"store-refs/{name}.md"),
            package.RootElement.GetProperty("storeReferences").EnumerateArray().Select(item => item.GetString()));
        foreach (var name in names.Append("clarification-first"))
            fixture.AssertDelivered(prepared, goal, name, Path.Combine(fixture.StoreRoot, "operator-evidence", name + ".md"), name);
        fixture.AssertAbsent(prepared, goal, "clarification-overflow");
        Assert.Equal(new[] { "STORE_REF_UNRESOLVED name=clarification-overflow reason=limit-exceeded" }, fixture.Diagnostics(goal));
    }

    [Fact]
    public void Answers_AtTotalExcerptLimit_ReportOverflowWithoutArtifact()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var (kernel, goal, task) = fixture.CreateGoal("Implement bounded evidence delivery.");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var names = Enumerable.Range(0, 4).Select(index => $"full-{index}").ToArray();
        foreach (var name in names) fixture.WriteSource($"operator-evidence/{name}.md", new string('x', 8000));
        fixture.WriteSource("operator-evidence/overflow.md", "one beyond total limit");
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which complete records?");
        kernel.SubmitHumanInput(request.Id, string.Join("\n", names.Append("overflow")
            .Select(name => WorkerStoreReferenceFixture.Reference(name))));

        var prepared = fixture.Prepare(kernel, goal, task);

        foreach (var name in names)
            fixture.AssertDelivered(prepared, goal, name, Path.Combine(fixture.StoreRoot, "operator-evidence", name + ".md"), new string('x', 8000));
        fixture.AssertAbsent(prepared, goal, "overflow");
        Assert.Equal(new[] { "STORE_REF_UNRESOLVED name=overflow reason=limit-exceeded" }, fixture.Diagnostics(goal));
    }

    [Fact]
    public void Answer_OverExcerptLimit_DeliversBoundedTextAndFullSourceHash()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var source = fixture.WriteSource("operator-evidence/large.md", new string('x', 8000) + "beyond excerpt limit");
        var (kernel, goal, task) = fixture.CreateGoal("Implement bounded evidence delivery.");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        fixture.Answer(kernel, goal, task.Id, "large");

        var prepared = fixture.Prepare(kernel, goal, task);

        fixture.AssertDelivered(prepared, goal, "large", source, new string('x', 8000));
        var delivered = WorkerStoreReferenceFixture.PromptArtifact(prepared, "context/store-refs/large.md");
        Assert.Contains("Truncated: yes; original-chars=8020; limit-chars=8000", delivered);
        Assert.DoesNotContain("beyond excerpt limit", delivered);
        Assert.Empty(fixture.Diagnostics(goal));
    }

    [Fact]
    public void SuccessfulPlannerPlan_CollisionWithAnswer_KeepsPlanPrecedence()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var source = fixture.WriteSource("operator-evidence/plan.md", "plan winner");
        fixture.WriteSource("operator-evidence/answer.md", "answer loser");
        var kernel = new AgentOrchestratorKernel(fixture.Clock);
        var planner = new TaskSpec(TaskId.New(), "Plan scope.", AgentRole.Planner);
        var task = new TaskSpec(TaskId.New(), "Implement scope.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Implement scope.", [planner, task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        File.WriteAllText(Path.Combine(fixture.WorkingDirectory, "seed.txt"), "fixture");
        var plan = WorkerDispatchTestSupport.PlannerContractPlanFixture() + "\n" + WorkerStoreReferenceFixture.Reference("chosen", "plan");
        Assert.True(PlannerOutputContract.TryValidatePlan(plan, fixture.WorkingDirectory,
            out var validatedPlan, out var validationDiagnostic), validationDiagnostic);
        var output = Path.Combine(fixture.Root, "planner.out.log");
        File.WriteAllText(output, "Planner completed.");
        Assert.True(PlannerOutputContract.TryPersistDurableReceipt(output, output, validatedPlan, out var diagnostic), diagnostic);
        kernel.RecordTaskVerification(goal.Id, planner.Id, new TaskVerificationRecord("planner", fixture.WorkingDirectory,
            0, "Planner completed.", "", fixture.Clock.UtcNow, StandardOutputPath: output));
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Plan recorded.");
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which evidence?");
        kernel.SubmitHumanInput(request.Id, WorkerStoreReferenceFixture.Reference("chosen", "answer"));

        var prepared = fixture.Prepare(kernel, goal, task);

        fixture.AssertDelivered(prepared, goal, "chosen", source, "plan winner");
        Assert.Equal(new[] { "STORE_REF_UNRESOLVED name=chosen reason=duplicate-name" }, fixture.Diagnostics(goal));
    }

    [Fact]
    public void Clarifications_AnswerTimesAndTies_UseDeterministicOrder()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var (kernel, goal, task) = fixture.CreateGoal("Implement deterministic evidence delivery.");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        foreach (var name in new[] { "early", "tie-a", "tie-b", "late" })
            fixture.WriteSource($"operator-evidence/{name}.md", name);
        var late = kernel.RequestHumanInput(goal.Id, task.Id, "Late answer?");
        var early = fixture.Answer(kernel, goal, task.Id, "early");
        fixture.Advance();
        var ties = new[] { fixture.Answer(kernel, goal, task.Id, "tie-a"), fixture.Answer(kernel, goal, task.Id, "tie-b") };
        fixture.Advance();
        kernel.SubmitHumanInput(late.Id, WorkerStoreReferenceFixture.Reference("late"));
        Assert.Equal(ties[0].AnsweredAt, ties[1].AnsweredAt);

        fixture.Prepare(kernel, goal, task);

        var ordered = new[] { early }.Concat(ties.OrderBy(item => item.Id.Value, StringComparer.Ordinal)).Append(late);
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Context(goal), "context-package.json")));
        Assert.Equal(ordered.Select(item => $"store-refs/{WorkerStoreReferenceResolver.Parse([item.Answer!]).Single().Name}.md"),
            package.RootElement.GetProperty("storeReferences").EnumerateArray().Select(item => item.GetString()));
    }
}
