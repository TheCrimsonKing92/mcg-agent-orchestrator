using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its harness and temporary question directory.
public sealed class OwnerConsoleMetadataOncePerRefreshTests
{
    [Theory]
    [InlineData("watch-transition")]
    [InlineData("acceptance")]
    [InlineData("loop-relaunch")]
    [InlineData("goal-escalation")]
    public async Task EventRefresh_BoardEvent_SharesOneMetadataRead(string eventKind)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var harness = new OwnerConsoleHarness();
            var goal = harness.AddGoal("11111111111111111111111111111111", "Build search", AgentRole.Developer);
            var session = Session(harness, root);
            await session.StartAsync(null, CancellationToken.None);
            AddQuestion(harness, goal.Id.Value);
            harness.State.Calls.Clear();
            var outputStart = harness.Output.Text.Length;

            await session.HandleEventAsync(new OwnerConductEvent(harness.Clock.GetUtcNow(),
                eventKind, goal.Id.Value, "running"), CancellationToken.None);

            Assert.Single(harness.State.Calls, call => call == "metadata");
            Assert.Contains("[1] 11111111 Should work continue?", harness.Output.Text[outputStart..]);
            Assert.Contains("board | active goals: 1 | owner questions: 1", harness.Output.Text[outputStart..]);
            Assert.Contains("11111111 | Build search | Active | Developer", harness.Output.Text[outputStart..]);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task BoardCommand_RepeatedRefreshes_ReadFreshMetadataOnceEach()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var harness = new OwnerConsoleHarness();
            var goal = harness.AddGoal("11111111111111111111111111111111", "Build search", AgentRole.Developer);
            var session = Session(harness, root);
            await session.StartAsync(null, CancellationToken.None);
            AddQuestion(harness, goal.Id.Value);

            for (var refresh = 0; refresh < 2; refresh++)
            {
                if (refresh == 1)
                    harness.AddGoal("22222222222222222222222222222222", "Review search", AgentRole.Reviewer);
                harness.State.Calls.Clear();
                var outputStart = harness.Output.Text.Length;

                Assert.True(await session.HandleCommandAsync("board", CancellationToken.None));

                Assert.Single(harness.State.Calls, call => call == "metadata");
                Assert.Contains($"board | active goals: {refresh + 1} | owner questions: 1",
                    harness.Output.Text[outputStart..]);
                if (refresh == 0)
                    Assert.Contains("[1] 11111111 Should work continue?", harness.Output.Text[outputStart..]);
                else
                    Assert.DoesNotContain("[1]", harness.Output.Text[outputStart..]);
                Assert.Contains("11111111 | Build search | Active | Developer", harness.Output.Text[outputStart..]);
                if (refresh == 1)
                    Assert.Contains("22222222 | Review search | Active | Reviewer", harness.Output.Text[outputStart..]);
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task Start_RealQuestionModel_SharesOneMetadataReadWithHeader()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var harness = new OwnerConsoleHarness();
            harness.AddGoal("11111111111111111111111111111111", "Build search", AgentRole.Developer);

            await Session(harness, root).StartAsync(null, CancellationToken.None);

            Assert.Single(harness.State.Calls, call => call == "metadata");
            Assert.Contains("conductor: running | active goals: 1 | owner questions: 0", harness.Output.Text);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static OwnerConsoleSession Session(OwnerConsoleHarness harness, string root) =>
        new(harness.State, new OwnerQuestionReadModel(harness.State, root), harness.Answers,
            harness.Liveness, harness.Digest, harness.Tail, harness.Output, harness.Clock);

    private static void AddQuestion(OwnerConsoleHarness harness, string goalId)
    {
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            HumanInputRequests =
            [
                new HumanInputRequestSnapshot("metadata-question", goalId, null, "Should work continue?",
                    harness.Clock.GetUtcNow(), HumanWaitKind.SpecClarification)
            ]
        });
    }
}
