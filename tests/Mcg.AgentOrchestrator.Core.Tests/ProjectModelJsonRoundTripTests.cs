using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: immutable values and read-only inspection of the verified checkout.
public sealed class ProjectModelJsonRoundTripTests
{
    [Fact(DisplayName = "JSON preserves nested facts, sources, questions and uncertainty")]
    public void RoundTripPreservesAllFacts()
    {
        var source = new FactSource("units/library.project", 3);
        var measured = new FactSource(measurementReference: "measurement:discovery/17");
        var model = new ProjectModel(1, "sample",
            [new ProjectUnit("library", new("Library", source, FactConfidence.High),
                new("units/library.project", source, FactConfidence.Medium), new(null, measured, FactConfidence.Low))],
            [new UnitDependency("tests", "library", source, FactConfidence.High)],
            [new UnitTestSetup("tests", new("sample-framework", source, FactConfidence.Medium),
                new("undetermined", measured, FactConfidence.Low))],
            [new ProjectOwnerQuestion("tests/runner", "Which runner should be used?", measured)]);

        var json = ProjectModelJson.Serialize(model);
        var restored = ProjectModelJson.Deserialize(json);

        Assert.Equal(json, ProjectModelJson.Serialize(restored));
        Assert.Equal(model.Units[0], Assert.Single(restored.Units));
        Assert.Equal(model.Dependencies[0], Assert.Single(restored.Dependencies));
        Assert.Equal(model.TestSetups[0], Assert.Single(restored.TestSetups));
        Assert.Equal(model.OwnerQuestions[0], Assert.Single(restored.OwnerQuestions));
        Assert.Contains("\"confidence\": \"low\"", json);
        Assert.Null(restored.Units[0].IsTest.Value);
        Assert.Equal("measurement:discovery/17", restored.TestSetups[0].Runner.Source.MeasurementReference);
    }

    [Fact(DisplayName = "Each model type has its own source file and fact provenance")]
    public void TypesHaveSeparateFilesAndFactProvenance()
    {
        Type[] types = [typeof(ProjectModel), typeof(ProjectUnit), typeof(ProjectFact<>), typeof(FactSource),
            typeof(FactConfidence), typeof(UnitDependency), typeof(UnitTestSetup), typeof(ProjectOwnerQuestion), typeof(ProjectModelJson)];
        var directory = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.Core", "Onboarding");
        Assert.All(types, type => Assert.True(File.Exists(Path.Combine(directory, type.Name.Split('`')[0] + ".cs")), type.Name));
        foreach (var type in new[] { typeof(ProjectFact<>), typeof(UnitDependency) })
        {
            Assert.Equal(typeof(FactSource), type.GetProperty("Source")!.PropertyType);
            Assert.Equal(typeof(FactConfidence), type.GetProperty("Confidence")!.PropertyType);
        }
        Assert.All(typeof(ProjectUnit).GetProperties().Where(property => property.Name != "Id"),
            property => Assert.Equal(typeof(ProjectFact<>), property.PropertyType.GetGenericTypeDefinition()));
        Assert.All(typeof(UnitTestSetup).GetProperties().Where(property => property.Name != "UnitId"),
            property => Assert.Equal(typeof(ProjectFact<>), property.PropertyType.GetGenericTypeDefinition()));
    }

    [Theory(DisplayName = "Invalid repository sources fail rather than accepting ambiguous provenance")]
    [InlineData("../outside", 1, null)]
    [InlineData("C:/outside", 1, null)]
    [InlineData("/outside", 1, null)]
    [InlineData("units\\library", 1, null)]
    [InlineData("units/library", 0, null)]
    [InlineData("units/library", 1, "measurement:one")]
    [InlineData(null, null, null)]
    [InlineData(null, 1, "measurement:one")]
    public void InvalidSourcesAreRejected(string? path, int? line, string? measurement) =>
        Assert.Throws<ArgumentException>(() => new FactSource(path, line, measurement));

    [Fact(DisplayName = "Facts reject missing sources and undefined confidence")]
    public void InvalidFactsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ProjectFact<string>("value", null!, FactConfidence.High));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectFact<string>("value", new("units/library", 1), (FactConfidence)100));
        Assert.Throws<ArgumentNullException>(() => new UnitDependency("tests", "library", null!, FactConfidence.High));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnitDependency("tests", "library", new("units/library", 1), (FactConfidence)100));
    }
}
