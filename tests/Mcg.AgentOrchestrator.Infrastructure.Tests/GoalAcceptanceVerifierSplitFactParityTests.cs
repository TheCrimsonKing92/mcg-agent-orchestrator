using System.Reflection;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierSplitFactParityTests
{
    private const string OriginalBuildSlotClass = nameof(GoalAcceptanceVerifierDotnetBuildSlotTests);

    [Xunit.Fact]
    public void SplitPreservesDeclaredFactAndTheoryMethodSet()
    {
        var expected = LoadBaseline();
        var expectedOwners = expected
            .Select(identity => identity[..identity.IndexOf('.', StringComparison.Ordinal)])
            .ToHashSet(StringComparer.Ordinal);

        var actual = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(type => IsAffectedConcreteTestClass(type, expectedOwners))
            .SelectMany(type => type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(IsFactOrTheory)
                .Select(method => $"{NormalizeOwner(type)}.{method.Name}"))
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Xunit.Fact]
    public void SplitPreservesCollectionConcurrencyContracts()
    {
        var assemblyTypes = Assembly.GetExecutingAssembly().GetTypes();
        var buildSlotFragments = assemblyTypes
            .Where(type =>
                type.IsClass &&
                !type.IsAbstract &&
                type.BaseType == typeof(GoalAcceptanceVerifierDotnetBuildSlotTests))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(11, buildSlotFragments.Length);
        Assert.All(
            buildSlotFragments,
            fragment => Assert.Equal(TestCollections.JobAccounting, CollectionName(fragment)));
        Assert.Equal(TestCollections.JobAccounting, CollectionName(typeof(RealProcessShardAlphaSmokeTests)));
        Assert.Equal(TestCollections.JobAccounting, CollectionName(typeof(RealProcessShardBetaSmokeTests)));

        Assert.Equal(
            TestCollections.GoalAcceptanceVerifier,
            CollectionName(typeof(GoalAcceptanceVerifierTests)));
        Assert.Equal(
            TestCollections.GoalAcceptanceVerifier,
            CollectionName(typeof(AcceptanceOutputCaptureTests)));
        Assert.Equal(
            TestCollections.GoalAcceptanceVerifier,
            CollectionName(typeof(HermeticVerificationEnvironmentTests)));
        Assert.Null(CollectionName(typeof(GoalAcceptanceVerifierDotnetBuildSlotTests)));
    }

    private static bool IsAffectedConcreteTestClass(Type type, IReadOnlySet<string> expectedOwners) =>
        type.IsClass &&
        !type.IsAbstract &&
        (expectedOwners.Contains(type.Name) ||
            type.Name.StartsWith(OriginalBuildSlotClass, StringComparison.Ordinal));

    private static bool IsFactOrTheory(MethodInfo method) =>
        method.GetCustomAttributes(inherit: false).Any(attribute => attribute is Xunit.FactAttribute);

    private static string NormalizeOwner(Type type) =>
        type.Name.StartsWith(OriginalBuildSlotClass, StringComparison.Ordinal)
            ? OriginalBuildSlotClass
            : type.Name;

    private static string? CollectionName(Type type) =>
        type.CustomAttributes
            .SingleOrDefault(attribute => attribute.AttributeType == typeof(Xunit.CollectionAttribute))?
            .ConstructorArguments
            .Single()
            .Value as string;

    private static string[] LoadBaseline(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "") =>
        File.ReadAllLines(Path.Combine(
                Path.GetDirectoryName(sourceFilePath)
                    ?? throw new InvalidOperationException("Parity test source path has no directory."),
                "GoalAcceptanceVerifierSplitFactBaseline.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
}
