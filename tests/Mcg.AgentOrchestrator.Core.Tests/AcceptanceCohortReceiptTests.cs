using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class AcceptanceCohortReceiptTests
{
    [Xunit.Fact(DisplayName = "AcceptanceCohortReceipt_exposes_test_project_keys")]
    public void AcceptanceCohortReceiptExposesTestProjectKeys()
    {
        const string coreKey = "ownership:test-project:tests/mcg.agentorchestrator.core.tests";
        const string infrastructureKey = "ownership:test-project:tests/mcg.agentorchestrator.infrastructure.tests";
        var identity = AcceptanceCohortIdentity.Create(
            [
                Binding(
                    "11111111111111111111111111111111",
                    'a',
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FirstTests.cs",
                    [infrastructureKey, "ownership:shared-infrastructure"]),
                Binding(
                    "22222222222222222222222222222222",
                    'b',
                    "tests/Mcg.AgentOrchestrator.Core.Tests/SecondTests.cs",
                    [coreKey, infrastructureKey.ToUpperInvariant(), "ownership:tests"])
            ],
            new string('c', 40),
            new string('d', 40),
            "manifest-v1");
        var receipt = new AcceptanceCohortReceipt(
            "receipt-1",
            identity,
            AcceptanceCohortGateOutcome.Passed,
            DateTimeOffset.UtcNow,
            1,
            [],
            0,
            []);

        Assert.Collection(
            receipt.TestProjectKeys,
            key => Assert.Equal(coreKey, key),
            key => Assert.Equal(infrastructureKey, key));
    }

    [Xunit.Fact(DisplayName = "AcceptanceCohortReceipt_ignores_historical_coarse_test_keys")]
    public void AcceptanceCohortReceiptIgnoresHistoricalCoarseTestKeys()
    {
        var identity = AcceptanceCohortIdentity.Create(
            [
                Binding("11111111111111111111111111111111", 'a', "tests/FirstTests.cs", ["ownership:tests"]),
                Binding("22222222222222222222222222222222", 'b', "src/Second.cs", ["ownership:source"])
            ],
            new string('c', 40),
            new string('d', 40),
            "manifest-v1");
        var receipt = new AcceptanceCohortReceipt(
            "receipt-2",
            identity,
            AcceptanceCohortGateOutcome.Failed,
            DateTimeOffset.UtcNow,
            1,
            [],
            1,
            []);

        Assert.Empty(receipt.TestProjectKeys);
    }

    private static AcceptanceCohortMemberBinding Binding(
        string goalValue,
        char revisionCharacter,
        string path,
        IReadOnlyList<string> resourceKeys) => new(
            new GoalId(goalValue),
            new string(revisionCharacter, 40),
            new string(revisionCharacter, 40),
            [path],
            resourceKeys,
            ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto,
            "Clean",
            "NoConflictsDetected");
}
