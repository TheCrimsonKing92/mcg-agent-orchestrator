using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorAcceptanceCohortFocusedAttributionTests
{
    private static readonly string[] Identities = ["Tests.First.Fails", "Tests.Second.Fails"];

    [Fact]
    public void Select_UsesProjectLabelledMethodRequests()
    {
        var checks = new[]
        {
            Check(false, Identities) with { TestProjectPath = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj" }
        };
        var selection = Assert.IsType<ConductorCohortFocusedSelection>(
            ConductorAcceptanceCohortFocusedAttribution.TrySelect(Identities, checks));
        Assert.Equal("Core.Tests: FullyQualifiedName~Tests.First.Fails; Core.Tests: FullyQualifiedName~Tests.Second.Fails", selection.Request);
        Assert.Equal(Identities, selection.Identities);
    }

    [Fact]
    public void Select_UnlabelledIdentitiesUseDefaultProject()
    {
        var selection = Assert.IsType<ConductorCohortFocusedSelection>(
            ConductorAcceptanceCohortFocusedAttribution.TrySelect(Identities, []));
        Assert.Equal("FullyQualifiedName~Tests.First.Fails; FullyQualifiedName~Tests.Second.Fails", selection.Request);
    }

    [Theory]
    [InlineData("Fails")]
    [InlineData("Tests.Fails(1)")]
    [InlineData("Tests.Fails[1]")]
    [InlineData("Tests..Fails")]
    [InlineData("Tests.Fails|FullyQualifiedName~Other")]
    [InlineData("")]
    public void Select_UnselectableIdentitySkipsFocusedPass(string identity) =>
        Assert.Null(ConductorAcceptanceCohortFocusedAttribution.TrySelect([identity], []));

    [Fact]
    public void Select_EmptyOversizedOrAmbiguousProjectSkipsFocusedPass()
    {
        Assert.Null(ConductorAcceptanceCohortFocusedAttribution.TrySelect([], []));
        Assert.Null(ConductorAcceptanceCohortFocusedAttribution.TrySelect(
            Enumerable.Range(0, 11).Select(index => $"Tests.Fails{index}").ToArray(), []));
        Assert.Null(ConductorAcceptanceCohortFocusedAttribution.TrySelect(Identities,
        [
            Check(false, Identities) with { TestProjectPath = "tests/One.csproj" },
            Check(false, Identities) with { TestProjectPath = "tests/Two.csproj" }
        ]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Interpret_ExecutedChecksPreserveFailuresAndCounts(bool passed)
    {
        var selection = new ConductorCohortFocusedSelection("request", Identities);
        var result = ConductorAcceptanceCohortFocusedAttribution.Interpret(
            new FocusedEvidenceRunResult("request", true, passed, "fixture", [Check(passed, passed ? [] : Identities)]), selection);
        Assert.Equal(ConductorCohortFocusedPassKind.Executed, result.Kind);
        Assert.Equal(passed ? Array.Empty<string>() : Identities, result.FailingIdentities);
        Assert.Equal(2, result.ExecutedCount);
        Assert.Equal(passed, result.AllChecksPassed);
        Assert.Equal(passed ? Array.Empty<string>() : ["focused"], result.FailedChecks);
    }

    [Fact]
    public void Interpret_RejectedRequestCannotAttribute()
    {
        var selection = new ConductorCohortFocusedSelection("request", Identities);
        var result = new FocusedEvidenceRunResult("request", false, false, "rejected", []);
        Assert.Equal(ConductorCohortFocusedPassKind.SelectionRejected,
            ConductorAcceptanceCohortFocusedAttribution.Interpret(result, selection).Kind);
    }

    [Fact]
    public void Interpret_InfrastructureOrIncompleteExecutionCannotAttribute()
    {
        var selection = new ConductorCohortFocusedSelection("request", Identities);
        var good = new FocusedEvidenceRunResult("request", true, true, "fixture", [Check(true, [])]);
        var unusable = new[]
        {
            good with { Checks = [] },
            good with { Checks = [Check(true, []) with { ExitCode = null }] },
            good with { Checks = [Check(true, []) with { ExecutedTestCount = null }] },
            good with { Checks = [Check(true, []) with { ExecutedTestCount = 0 }] },
            good with { Checks = [Check(true, []) with { ExecutedTestCount = 1 }] },
            good with { OutcomeReason = FindingEvidenceOutcomeReason.ApparatusFailure },
            good with { Checks = [Check(false, Identities) with
                { CompletionDecision = new(false, AcceptanceShardCompletionPredicates.TimedOut, true, 1, 2, 2, "Failed") }] },
            good with { Checks = [Check(false, Identities) with
                { CompletionDecision = new(false, AcceptanceShardCompletionPredicates.IncompleteExecution, false, 1, 3, 2, "Failed") }] },
            good with { Checks = [Check(false, Identities) with { FailureClassification = AcceptanceFailureClassifications.GateEnvironmentInterference }] }
        };
        Assert.All(unusable, result => Assert.Equal(ConductorCohortFocusedPassKind.InfrastructureFailure,
            ConductorAcceptanceCohortFocusedAttribution.Interpret(result, selection).Kind));
    }

    [Fact]
    public void Decide_OnlySingleCompleteReproducerAndFullyGreenPeerAttribute()
    {
        var red = Pass(Identities);
        var green = Pass([]);
        Assert.Equal(0, ConductorAcceptanceCohortFocusedAttribution.Decide(Identities, red, green));
        Assert.Equal(1, ConductorAcceptanceCohortFocusedAttribution.Decide(Identities, green, red));
        Assert.Equal(0, ConductorAcceptanceCohortFocusedAttribution.Decide(Identities,
            Pass([.. Identities, "Tests.Unrelated.Fails"]), green));
    }

    [Fact]
    public void Decide_EveryNonDispositiveCombinationFallsBack()
    {
        var red = Pass(Identities);
        var green = Pass([]);
        var cases = new (ConductorCohortFocusedPassResult First, ConductorCohortFocusedPassResult Second)[]
        {
            (red, red), (green, green), (Pass([Identities[0]]), Pass([Identities[1]])),
            (Pass([Identities[0]]), green), (red, green with { AllChecksPassed = false }),
            (red, green with { ExecutedCount = 1 }), (red, Pass(["Tests.Unrelated.Fails"])),
            (red with { Kind = ConductorCohortFocusedPassKind.InfrastructureFailure }, green),
            (red, green with { Kind = ConductorCohortFocusedPassKind.InfrastructureFailure }),
            (red, green with { Kind = ConductorCohortFocusedPassKind.SelectionRejected })
        };
        Assert.All(cases, pair => Assert.Null(
            ConductorAcceptanceCohortFocusedAttribution.Decide(Identities, pair.First, pair.Second)));
        Assert.Null(ConductorAcceptanceCohortFocusedAttribution.Decide([], red, green));
    }

    private static AcceptanceCheckResult Check(bool passed, IReadOnlyList<string> failures) =>
        new("focused", passed, passed ? 0 : 1, null, FailingTestIdentities: failures, ExecutedTestCount: 2);

    private static ConductorCohortFocusedPassResult Pass(IReadOnlyList<string> failures) =>
        new(ConductorCohortFocusedPassKind.Executed, failures, failures.Count == 0 ? [] : ["focused"],
            2, failures.Count == 0, []);
}
