using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class ProspectiveEvidenceGateResolverScopeTests
{
    [Theory]
    [InlineData("operator", "names", HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData("Acceptance", "null", HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData(null, "names", HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData("acceptance-gate", "empty", HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData("acceptance-gate", "null", HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData("acceptance-gate", "names", HumanWaitKind.SpecClarification)]
    public void ExcludedWaitRemainsUntouchedDespitePassingEvidence(string? owner, string namesMode, HumanWaitKind kind)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        string[]? names = namesMode switch
        {
            "empty" => [], "null" => null, _ => [ProspectiveEvidenceGateFixture.IncidentClass]
        };
        var wait = fixture.CreateWait(names, owner, kind);
        fixture.WriteAttempt(ProspectiveEvidenceGateFixture.PassingRows);

        var result = fixture.Resolve();

        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        Assert.Empty(wait.AnswerHistory);
        Assert.Empty(result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
    }

    [Theory]
    [InlineData("Acceptance")]
    [InlineData("acceptance")]
    [InlineData("ACCEPTANCE-GATE")]
    public void AcceptanceAlias_ResolvesSelectorsAndPreservesExcludedSiblings(string owner)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var eligible = fixture.CreateWait([ProspectiveEvidenceGateFixture.IncidentClass], owner);
        var selectorless = fixture.CreateWait(null, owner);
        var operatorWait = fixture.CreateWait([ProspectiveEvidenceGateFixture.IncidentClass], "operator");
        fixture.WriteAttempt(ProspectiveEvidenceGateFixture.PassingRows);

        var result = fixture.Resolve();

        Assert.Equal([eligible.Id], result.ResolvedRequestIds);
        Assert.True(eligible.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, eligible.AuthoritativeAnswer!.Origin);
        Assert.Empty(result.UnresolvedWaits);
        Assert.Equal(new[] { selectorless.Id, operatorWait.Id }.OrderBy(id => id.Value),
            fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id).Select(wait => wait.Id).OrderBy(id => id.Value));
        Assert.All(new[] { selectorless, operatorWait }, wait =>
        {
            Assert.False(wait.IsCompleted);
            Assert.Null(wait.Answer);
            Assert.Empty(wait.AnswerHistory);
        });
    }

    [Fact]
    public void AutomatedAnswerDoesNotCompleteExcludedSiblingWithSameFingerprint()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var eligible = fixture.CreateWait([ProspectiveEvidenceGateFixture.IncidentClass], "ACCEPTANCE-GATE");
        fixture.WriteAttempt(ProspectiveEvidenceGateFixture.PassingRows);
        var snapshot = fixture.Kernel.ExportSnapshot();
        var siblingId = HumanInputRequestId.New();
        var sibling = Assert.Single(snapshot.HumanInputRequests) with { Id = siblingId.Value, EvidenceOwner = "operator" };
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            HumanInputRequests = snapshot.HumanInputRequests.Append(sibling).ToArray()
        });

        var result = ProspectiveEvidenceGateResolver.Resolve(kernel, kernel.GetGoal(fixture.Goal.Id),
            fixture.OrchestratorDirectory, fixture.CandidateSha);

        Assert.Equal([eligible.Id], result.ResolvedRequestIds);
        var remaining = Assert.Single(kernel.GetPendingHumanInput(fixture.Goal.Id));
        Assert.Equal(siblingId, remaining.Id);
        Assert.False(remaining.IsCompleted);
        Assert.Null(remaining.Answer);
        Assert.Null(remaining.SupersededByRequestId);
    }

    [Fact]
    public void CompletedWaitKeepsOriginalAnswer()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([ProspectiveEvidenceGateFixture.IncidentClass]);
        fixture.Kernel.SubmitHumanInput(wait.Id, "Existing operator receipt");
        fixture.WriteAttempt(ProspectiveEvidenceGateFixture.PassingRows);

        var result = fixture.Resolve();

        Assert.Empty(result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
        Assert.Equal("Existing operator receipt", wait.Answer);
        Assert.Equal(HumanInputAnswerOrigin.Operator, wait.AuthoritativeAnswer!.Origin);
    }
}
