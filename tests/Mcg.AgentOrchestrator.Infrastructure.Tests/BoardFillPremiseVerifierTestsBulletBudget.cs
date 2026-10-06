using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its files and SQLite store; the process delegate is local.
public sealed class BoardFillPremiseVerifierTestsBulletBudget
{
    private const string Answer = """
        {"verdicts":[{"bullet":1,"verdict":"verified","evidence":"src/Feature/File.cs:1"}]}
        """;
    private static ModelFunctionCatalog Catalog() => new([new(
        ModelFunctionPurposes.BoardFillVerifier, ModelLane.Capable,
        new ModelProfile("test", "unused", ModelCapability.Text, SubscriptionMode.ApiKey),
        Subscription: new("codex-cli", "verifier-alias", "high"))]);

    [Theory]
    [InlineData(9, 11)]
    [InlineData(1, 3)]
    public async Task Premise_bullets_size_the_captured_run_timeout(int bulletCount, int minutes)
    {
        using var h = new BoardFillAssessmentTestFixture();
        WorkerProcessRunRequest? captured = null;
        var verifier = new BoardFillPremiseVerifier(() => Catalog(),
            new BoardFillAssessmentTestFixture.Repository(), h.Root,
            (request, _) => { captured = request; return Task.FromResult(new PanelProcessResult(0, Answer, "")); });
        var premise = "## Measured premise\n" +
            string.Join("\n", Enumerable.Range(1, bulletCount).Select(index => $"- bullet {index}"));
        Assert.Equal(bulletCount, BoardFillVerifierContract.BulletCount(premise));

        await verifier.VerifyAsync(premise, BoardFillAssessmentTestFixture.Head, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(BoardFillVerifierBudget.For(bulletCount), captured.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(minutes), captured.Timeout);
        if (bulletCount == 9) Assert.NotEqual(TimeSpan.FromMinutes(3), captured.Timeout);
    }
}
