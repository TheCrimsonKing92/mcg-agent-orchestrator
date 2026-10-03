using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorAcceptanceCohortMainSuspectTests
{
    [Theory]
    [InlineData(AcceptanceCohortAttributionOutcome.FirstMemberFailed)]
    [InlineData(AcceptanceCohortAttributionOutcome.SecondMemberFailed)]
    [InlineData(AcceptanceCohortAttributionOutcome.InteractionOnly)]
    [InlineData(AcceptanceCohortAttributionOutcome.Indeterminate)]
    [InlineData(AcceptanceCohortAttributionOutcome.NotApplicable)]
    public void OtherOutcomes_DoNotResolveSources(AcceptanceCohortAttributionOutcome outcome)
    {
        var classified = Classification(outcome, ["Tests.Shared.Fails"], ["Tests.Shared.Fails"]);
        Assert.Null(ConductorAcceptanceCohortMainSuspect.TryDecide(classified, Bindings(),
            Path.GetTempPath(), _ => throw new InvalidOperationException("Ineligible outcome resolved sources.")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyOrUnequalSets_DoNotResolveSources(bool empty)
    {
        var classified = Classification(AcceptanceCohortAttributionOutcome.BothMembersFailed,
            empty ? [] : ["Tests.Shared.A"], empty ? [] : ["Tests.Shared.A", "Tests.Shared.B"]);
        Assert.Null(ConductorAcceptanceCohortMainSuspect.TryDecide(classified, Bindings(),
            Path.GetTempPath(), _ => throw new InvalidOperationException("Ineligible sets resolved sources.")));
    }

    [Fact]
    public void EqualSets_ResolveRealSourceAndCacheByClass()
    {
        var root = Path.Combine(Path.GetTempPath(), $"main-suspect-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        try
        {
            File.WriteAllText(Path.Combine(root, "tests", "Shared.cs"), "public class Shared { }");
            var first = new[] { "Tests.Shared.A", "Tests.Shared.B" };
            var classified = Classification(AcceptanceCohortAttributionOutcome.BothMembersFailed,
                first, ["Tests.Shared.B", "Tests.Shared.A"]);
            var resolutions = 0;
            var shared = ConductorAcceptanceCohortMainSuspect.TryDecide(classified, Bindings(), root, test =>
            {
                resolutions++;
                return AcceptanceTestSourceResolver.ResolveSourcePaths(root, null, test);
            });
            Assert.Equal(first, shared);
            Assert.Equal(1, resolutions);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(0, "./TESTS\\Shared.cs")]
    [InlineData(1, "tests/Shared.cs")]
    public void AnyMemberTouchesAnyResolvedSource_DoesNotSuspectMain(int owner, string changed)
    {
        var root = Path.GetTempPath();
        var bindings = Bindings();
        bindings[owner] = Bind(owner, changed);
        var classified = Classification(AcceptanceCohortAttributionOutcome.BothMembersFailed,
            ["Tests.Shared.A"], ["Tests.Shared.A"]);
        Assert.Null(ConductorAcceptanceCohortMainSuspect.TryDecide(classified, bindings, root,
            _ => ["tests/Other.cs", Path.Combine(root, "tests", "Shared.cs")]));
    }

    [Fact]
    public void AnyUnresolvedTest_PreventsMainSuspect()
    {
        var classified = Classification(AcceptanceCohortAttributionOutcome.BothMembersFailed,
            ["Tests.Shared.A", "Tests.Unknown.A"], ["Tests.Shared.A", "Tests.Unknown.A"]);
        Assert.Null(ConductorAcceptanceCohortMainSuspect.TryDecide(classified, Bindings(), Path.GetTempPath(),
            test => test.Contains("Unknown", StringComparison.Ordinal) ? [] : ["tests/Shared.cs"]));
    }

    [Fact]
    public void DiagnosticList_BoundsTestCountAndRemovesWhitespace()
    {
        var tests = Enumerable.Range(0, 12).Select(index => $"Tests.Shared.Fails({index}, arg)\n").ToArray();
        var formatted = ConductorAcceptanceCohortMainSuspect.FormatTests(tests);
        Assert.Contains("Fails(9,_arg)_", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("Fails(10", formatted, StringComparison.Ordinal);
        Assert.EndsWith(" more=2", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', formatted);
        Assert.Equal("main-suspect", ConductorAcceptanceCohortMainSuspect.FailureToken);
    }

    private static ConductorAcceptanceCohortIdentityAttribution Classification(
        AcceptanceCohortAttributionOutcome outcome, string[] first, string[] second) =>
        new(outcome,
        [new(new GoalId(new string('1', 32)), 0, new string('a', 40), first),
         new(new GoalId(new string('2', 32)), 1, new string('b', 40), second)], []);

    private static AcceptanceCohortMemberBinding[] Bindings() =>
        [Bind(0, "src/First.cs"), Bind(1, "src/Second.cs")];

    private static AcceptanceCohortMemberBinding Bind(int ordinal, string path) =>
        new(new GoalId(new string(ordinal == 0 ? '1' : '2', 32)), new string('a', 40), new string('b', 40),
            [path], ["production:test"], ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "ready", "ready");
}
