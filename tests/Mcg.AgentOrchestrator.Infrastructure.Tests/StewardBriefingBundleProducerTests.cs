using Mcg.AgentOrchestrator.Infrastructure;

public sealed class StewardBriefingBundleProducerTests
{
    [Xunit.Fact(DisplayName = "StewardBriefingBundleProducer_populates_every_bundle_field_from_inputs")]
    public void StewardBriefingBundleProducerPopulatesEveryBundleFieldFromInputs()
    {
        var raisedAt = DateTimeOffset.Parse("2026-07-22T12:00:00Z");
        var escalation = new StewardEscalationItem(
            "inbox-1",
            StewardEscalationCategory.Normal,
            "goal-123456",
            "FailedTask",
            "cause-a",
            4,
            raisedAt,
            "Task failed",
            "exit=1");
        var goalTask = new StewardGoalTaskRecord("goal-123456", "task-1", "Failed", 2, 1, 1);
        var receipts = new StewardNumericReceiptSummary(7, 2, [0, 1, 2], 3, 21, 5);
        var precedent = new StewardPrecedentMatch("FailedTask", "cause-a", 5, 4);
        var policy = new StewardPolicySnapshot("safe-auto", ["land-deny", "billing-deny"], raisedAt.AddMinutes(-30));
        var budget = new StewardInterruptBudgetLedger(6, 2, 4, raisedAt.Date);
        var provenance = new StewardProvenanceLink("tester", "trx-1", 1, raisedAt.AddMinutes(-10));
        var prose = new StewardQuotedWorkerProse("developer", "The command failed after compiling.");
        var inputs = new StewardBriefingBundleInputs(
            [escalation],
            [goalTask],
            receipts,
            new Dictionary<StewardPrecedentKey, StewardPrecedentMatch>
            {
                [new StewardPrecedentKey(precedent.Kind, precedent.CauseFingerprint)] = precedent
            },
            policy,
            budget,
            [provenance],
            [prose]);

        var bundle = new StewardBriefingBundleProducer().Produce(inputs);

        Assert.Equal([escalation], bundle.Escalations);
        Assert.Equal([goalTask], bundle.GoalTasks);
        Assert.Equal(receipts, bundle.Receipts);
        Assert.Equal([precedent], bundle.MatchedPrecedents);
        Assert.Equal(policy, bundle.PolicySnapshot);
        Assert.Equal(budget, bundle.InterruptBudget);
        Assert.Equal([provenance], bundle.FailingRoundProvenance);
        Assert.Equal([prose], bundle.WorkerProse);
        Assert.NotEmpty(bundle.InputsHash());
    }

    [Xunit.Fact(DisplayName = "StewardBriefingBundleProducer_has_no_store_dependency_or_write_surface")]
    public void StewardBriefingBundleProducerHasNoStoreDependencyOrWriteSurface()
    {
        var producerType = typeof(StewardBriefingBundleProducer);
        var referencedTypes = producerType
            .GetConstructors()
            .SelectMany(ctor => ctor.GetParameters().Select(parameter => parameter.ParameterType))
            .Concat(producerType.GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public)
                .Select(field => field.FieldType))
            .ToList();

        Assert.Empty(referencedTypes.Where(IsStoreType));

        var produce = Assert.Single(producerType.GetMethods().Where(method => method.Name == nameof(StewardBriefingBundleProducer.Produce)));
        Assert.Equal(typeof(StewardBriefingBundle), produce.ReturnType);
        var parameter = Assert.Single(produce.GetParameters());
        Assert.Equal(typeof(StewardBriefingBundleInputs), parameter.ParameterType);
    }

    private static bool IsStoreType(Type type) =>
        type.Name.Contains("Store", StringComparison.OrdinalIgnoreCase) ||
        type.GetInterfaces().Any(item => item.Name.Contains("Store", StringComparison.OrdinalIgnoreCase));
}
