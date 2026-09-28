using Mcg.AgentOrchestrator.Core;

public sealed class DeveloperDeferredTestClassNamesFormsTests
{
    [Theory]
    [InlineData("tests: deferred naming A, B", new[] { "A", "B" })]
    [InlineData("tests: deferred naming A and B", new[] { "A", "B" })]
    [InlineData("tests: deferred — A; conductor executes tests.", new[] { "A" })]
    [InlineData("tests: deferred – A, B", new[] { "A", "B" })]
    [InlineData("tests: deferred for A", new[] { "A" })]
    [InlineData("deferred naming: A", new[] { "A" })]
    [InlineData("deferred of A, B and C", new[] { "A", "B", "C" })]
    [InlineData("deferred `A` and `B`", new[] { "A", "B" })]
    public void ParsesNamedClasses(string field, string[] expected) =>
        Assert.Equal(expected, DeveloperDeferredTestClassNames.Parse(field));

    [Theory]
    [InlineData("deferred - ConductorDriverTests appears only in prose")]
    [InlineData("deferred - no named classes")]
    [InlineData("deferred - request of A, B")]
    [InlineData("deferred naming Nothing appears only in prose")]
    public void RejectsProse(string field) =>
        Assert.Empty(DeveloperDeferredTestClassNames.Parse(field));
}
