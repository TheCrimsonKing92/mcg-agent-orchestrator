using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class StewardShadowRecommendationStoreTests
{
    [Fact]
    public async Task SqliteStoreAppendsImmutableRecommendationsAndOneAgreement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "steward-shadow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = SqliteStewardShadowRecommendationStore.ForDirectory(directory);
            var now = DateTimeOffset.Parse("2026-09-24T18:00:00Z");
            var receipt = new StewardShadowRecommendation("receipt-1",
                StewardShadowEscalationClass.PlannerOutputContractRejected, "escalation-1",
                new StewardShadowDecision("retry", [], "goal-1", "task-1", "ContractClarification", false, "original"),
                "inputs-hash", now);
            await store.AppendRecommendationAsync(receipt);
            await store.AppendRecommendationAsync(receipt with { Decision = receipt.Decision with { Text = "changed" } });
            var agreement = new StewardShadowAgreementRecord("receipt-1", "inputs-hash",
                receipt.Class, "intent-1", "retry", StewardShadowAgreementOutcome.Match, [], null, null,
                now.AddMinutes(1));
            await store.AppendAgreementAsync(agreement);
            await store.AppendAgreementAsync(agreement with { Outcome = StewardShadowAgreementOutcome.Mismatch });

            var reopened = SqliteStewardShadowRecommendationStore.ForDirectory(directory);
            Assert.Equal("original", Assert.Single(await reopened.ListRecommendationsAsync()).Decision.Text);
            Assert.Equal("inputs-hash", Assert.Single(await reopened.ListAgreementsAsync()).RecommendationInputsHash);
            Assert.Equal(StewardShadowAgreementOutcome.Match, Assert.Single(await reopened.ListAgreementsAsync()).Outcome);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
