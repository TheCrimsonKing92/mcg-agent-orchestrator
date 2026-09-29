using System.Reflection;

public sealed class SeedIsolationConsolidationTests
{
    [Fact]
    public void OwnershipAndSeparationFactsAreCombinedIntoDeletionFact()
    {
        var testClass = typeof(GoalWorktreeTestsSeedIsolation);
        Assert.Null(testClass.GetMethod("SeedRepositoriesDoNotShareAMutableParentDirectory"));
        Assert.Null(testClass.GetMethod("SeedRootIsOwnedByTheCurrentProcess"));
        Assert.NotNull(testClass.GetMethod(
            nameof(GoalWorktreeTestsSeedIsolation.DeletingOneSeedRepositoryLeavesOtherTreesIntact)));
    }

    [Fact]
    public void RepeatedConcurrencyIsHostIntegrationOnly()
    {
        var testClass = typeof(GoalWorktreeTestsSeedIsolation);
        var inGate = testClass.GetMethod(
            nameof(GoalWorktreeTestsSeedIsolation.ConcurrentSeedCreationAllocatesDisjointContainers));
        var repeated = testClass.GetMethod(
            nameof(GoalWorktreeTestsSeedIsolation.ConcurrentSeedCreationRepeatedlyProducesResolvableHeads));
        Assert.NotNull(inGate);
        Assert.NotNull(repeated);
        Assert.False(IsHostIntegration(testClass.GetCustomAttributesData()));
        Assert.False(IsHostIntegration(inGate.GetCustomAttributesData()));
        Assert.True(IsHostIntegration(repeated.GetCustomAttributesData()));

        var inGateMethods = testClass.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name.StartsWith("ConcurrentSeedCreation", StringComparison.Ordinal))
            .Where(method => !IsHostIntegration(method.GetCustomAttributesData()))
            .ToArray();
        Assert.Single(inGateMethods);
        Assert.Same(inGate, inGateMethods[0]);
    }

    private static bool IsHostIntegration(IEnumerable<CustomAttributeData> attributes) =>
        attributes.Any(attribute =>
            attribute.AttributeType == typeof(Xunit.TraitAttribute) &&
            attribute.ConstructorArguments.Count == 2 &&
            Equals(attribute.ConstructorArguments[0].Value, "Category") &&
            Equals(attribute.ConstructorArguments[1].Value, "HostIntegration"));
}
