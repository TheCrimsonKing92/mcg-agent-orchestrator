using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class RealProcessShardAlphaSmokeTests : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact]
    public Task SynchronizesWithBetaShard() =>
        GoalAcceptanceVerifierDotnetBuildSlotTests.SynchronizeRealProcessShardSmokeAsync(
            "MCG_SHARD_SMOKE_ALPHA_SIGNAL",
            "MCG_SHARD_SMOKE_BETA_SIGNAL");
}
