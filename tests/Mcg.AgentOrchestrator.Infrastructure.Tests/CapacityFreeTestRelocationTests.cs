using System.Reflection;

public sealed class CapacityFreeTestRelocationTests
{
    [Fact]
    public void RelocatedClassesDoNotAcquireHostCapacity()
    {
        foreach (var testClass in new[]
        {
            typeof(AcceptanceMainAdvanceClassifierRepositoryWideTests),
            typeof(SeedRepositoryContainerNameTests)
        })
        {
            Assert.Equal(typeof(object), testClass.BaseType);
            for (var baseType = testClass.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                Assert.NotEqual(typeof(HostCapacityBoundTestBase), baseType);
            }
        }
    }

    [Fact]
    public void ClassifierTheoriesKeepTheOriginalTenCasesAndExpectedKinds()
    {
        var expected = new (string Path, string Kind)[]
        {
            ("Directory.Build.props", "BuildSystem"),
            ("src/bin/generated.cs", "GeneratedOrNoisy"),
            ("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "SharedInfrastructure"),
            ("scripts/verify.ps1", "Script"),
            ("unmapped/file.bin", "Unknown")
        };

        Assert.Equal(expected, InlineRows(nameof(AcceptanceMainAdvanceClassifierRepositoryWideTests.Classifier_RepositoryWideLanding_FailsClosed)));
        Assert.Equal(expected, InlineRows(nameof(AcceptanceMainAdvanceClassifierRepositoryWideTests.Classifier_RepositoryWideCandidate_FailsClosed)));
        Assert.Null(typeof(AcceptanceVerdictCarryForwardTests).GetMethod(
            nameof(AcceptanceMainAdvanceClassifierRepositoryWideTests.Classifier_RepositoryWideLanding_FailsClosed)));
        Assert.Null(typeof(AcceptanceVerdictCarryForwardTests).GetMethod(
            nameof(AcceptanceMainAdvanceClassifierRepositoryWideTests.Classifier_RepositoryWideCandidate_FailsClosed)));
    }

    [Fact]
    public void ProcessGenerationFactLivesOnlyOnTheCapacityFreeClass()
    {
        var factName = nameof(SeedRepositoryContainerNameTests.ProcessGenerationsDoNotReuseContainerNamesWhenCounterRestarts);
        Assert.NotNull(typeof(SeedRepositoryContainerNameTests).GetMethod(factName));
        Assert.Null(typeof(GoalWorktreeTestsSeedIsolation).GetMethod(factName));
    }

    private static (string Path, string Kind)[] InlineRows(string methodName)
    {
        var method = typeof(AcceptanceMainAdvanceClassifierRepositoryWideTests).GetMethod(methodName);
        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttribute<Xunit.TheoryAttribute>());
        return method.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType == typeof(Xunit.InlineDataAttribute))
            .Select(attribute =>
            {
                var values = (IEnumerable<CustomAttributeTypedArgument>)attribute.ConstructorArguments.Single().Value!;
                var row = values.Select(value => Assert.IsType<string>(value.Value)).ToArray();
                Assert.Equal(2, row.Length);
                return (row[0], row[1]);
            })
            .ToArray();
    }
}
