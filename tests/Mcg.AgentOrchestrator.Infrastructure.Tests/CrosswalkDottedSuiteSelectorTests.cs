using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure selector resolution over in-memory discovery.
public sealed class CrosswalkDottedSuiteSelectorTests
{
    private static readonly string[] Ids =
    [
        "spec/a_spec.lua::PZISCollectionUtils.Set add adds a member",
        "spec/b_spec.lua::PZISStringUtils createSearchPattern escapes",
        "spec/b_spec.lua::PZISStringUtils trim strips"
    ];

    [Theory]
    [InlineData("PZISCollectionUtils.Set.add", 0)]
    [InlineData("PZISStringUtils.createSearchPattern", 1)]
    public void DottedMethod_MixedSeparators_SelectsMatchingTest(string selector, int index)
    {
        var resolution = CrosswalkSelectorResolution.Resolve([selector], Ids);

        Assert.Equal([Ids[index]], resolution.Selected);
        Assert.Empty(resolution.Refused);
    }

    [Fact]
    public void BareSuite_TwoNames_SelectsBothTests()
    {
        Assert.Equal([Ids[1], Ids[2]], CrosswalkFocusedEvidence.ExpandSelectors(["PZISStringUtils"], Ids));
    }

    [Theory]
    [InlineData("PZISStringUtils.create")]
    [InlineData("pzisstringutils.createSearchPattern")]
    [InlineData("PZISCollectionUtils.Se")]
    public void Prefix_WithoutOrdinalWordBoundary_IsRefused(string selector)
    {
        var resolution = CrosswalkSelectorResolution.Resolve([selector], Ids);

        Assert.Empty(resolution.Selected);
        Assert.Equal([selector], resolution.Refused);
    }

    [Theory]
    [InlineData("A.B.C", "A.B.C")]
    [InlineData("A.B.C", "A B.C")]
    [InlineData("A.B.C", "A.B C")]
    [InlineData("A.B.C", "A B C")]
    [InlineData("Δ_2.٣B", "Δ_2 ٣B")]
    [InlineData("λ.Ж", "λ Ж")]
    [InlineData("A_.B_", "A_ B_")]
    [InlineData("A.B.C.D.E.F.G.H.I", "A B C D E F G H I")]
    [InlineData(".A.B.C.D.E.F.G.H.I.", ".A B C D E F G H I.")]
    public void EligibleDots_EachSubsetAndEightDotLimit_Matches(string selector, string name)
    {
        var id = "spec/example.lua::" + name;

        Assert.Equal([id], CrosswalkFocusedEvidence.ExpandSelectors([selector], [id]));
    }

    [Theory]
    [InlineData("A..B", "A  B")]
    [InlineData("A.-B", "A -B")]
    [InlineData(".A", " A")]
    [InlineData("A.", "A ")]
    [InlineData("A. B", "A  B")]
    public void IneligibleDot_StaysLiteral(string selector, string replaced)
    {
        var literal = "spec/example.lua::" + selector + " case";
        var other = "spec/example.lua::" + replaced + " case";

        Assert.Equal([literal], CrosswalkFocusedEvidence.ExpandSelectors([selector], [literal, other]));
    }

    [Fact]
    public void NineEligibleDots_OnlyLiteralVariantMatches()
    {
        const string selector = "A.B.C.D.E.F.G.H.I.J";
        var literal = "spec/example.lua::" + selector + " case";
        const string partial = "spec/example.lua::A B.C.D.E.F.G.H.I.J case";
        const string spaced = "spec/example.lua::A B C D E F G H I J case";

        Assert.Equal([literal], CrosswalkFocusedEvidence.ExpandSelectors([selector], [literal, partial, spaced]));
    }
}
