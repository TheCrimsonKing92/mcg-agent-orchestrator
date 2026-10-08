using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: fixture-owned declarations and deterministic fake execution only.
public sealed class UnitMeasurementDiscoveryTests
{
    [Fact]
    public void MeasurementsAreAbsentWithoutExplicitExecution()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var adapter = new DotnetProjectDiscoveryAdapter();
        Assert.Empty(adapter.Discover(fixture.Root).Measurements);
        var forbidden = new RecordingUnitCommandMeasurer { Result = (_, _) => throw new InvalidOperationException("Unexpected execution") };
        Assert.Empty(adapter.Discover(fixture.Root, measurer: forbidden, measuredKinds: UnitCommandKinds.None).Measurements);
        Assert.Empty(forbidden.Calls);
    }

    [Fact]
    public void FixedDurationsProduceExactCallsAndMeasurementReferences()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var fake = new RecordingUnitCommandMeasurer();
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root, measurer: fake);
        Assert.Equal(new[] {
            (model.Commands[0].UnitId, UnitCommandKinds.Build),
            (model.Commands[1].UnitId, UnitCommandKinds.Build), (model.Commands[1].UnitId, UnitCommandKinds.Test),
            (model.Commands[2].UnitId, UnitCommandKinds.Build), (model.Commands[2].UnitId, UnitCommandKinds.Test)
        }, fake.Calls.Select(call => (call.UnitId, call.Kind)));
        Assert.All(fake.Calls, call =>
        {
            Assert.Equal(fixture.Root, call.Root);
            var unit = model.Commands.Single(unit => unit.UnitId == call.UnitId);
            Assert.Equal(call.Kind == UnitCommandKinds.Build ? unit.BuildCommand.Value : unit.TestCommand!.Value, call.Command);
        });
        Assert.Equal(model.Units.Select(unit => unit.Id), model.Measurements.Select(unit => unit.UnitId));
        foreach (var measurement in model.Measurements)
        {
            AssertDuration(measurement.BuildSeconds!, 12.5, measurement.UnitId, "build");
            if (model.Commands.Single(unit => unit.UnitId == measurement.UnitId).TestCommand is null)
                Assert.Null(measurement.TestSeconds);
            else AssertDuration(measurement.TestSeconds!, 3.25, measurement.UnitId, "test");
        }
        Assert.Empty(model.OwnerQuestions);
    }

    [Fact]
    public void FailedBuildLeavesFactAbsentAndSkipsDependentTest()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        const string failed = "tests/Sample.MtpTests/Sample.MtpTests.csproj";
        var fake = new RecordingUnitCommandMeasurer { Result = (id, _) => id == failed
            ? UnitCommandMeasurementResult.Failed("fixture failure") : UnitCommandMeasurementResult.Succeeded(7) };
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root, measurer: fake);
        Assert.DoesNotContain(model.Measurements, unit => unit.UnitId == failed);
        Assert.DoesNotContain(fake.Calls, call => call.UnitId == failed && call.Kind == UnitCommandKinds.Test);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"commands/{failed}/build/duration");
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"commands/{failed}/test/duration");
        Assert.Equal(2, model.Measurements.Count);
    }

    [Fact]
    public void FailedTestPreservesSuccessfulBuild()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var fake = new RecordingUnitCommandMeasurer { Result = (_, kind) => kind == UnitCommandKinds.Test
            ? UnitCommandMeasurementResult.Failed("fixture failure") : UnitCommandMeasurementResult.Succeeded(7) };
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root, measurer: fake);
        Assert.Equal(3, model.Measurements.Count);
        Assert.All(model.Measurements, unit => { Assert.Equal(7, unit.BuildSeconds!.Value); Assert.Null(unit.TestSeconds); });
        foreach (var unit in model.Commands.Where(unit => unit.TestCommand is not null))
            Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"commands/{unit.UnitId}/test/duration");
    }

    [Fact]
    public void UnknownCommandIsSkippedAndQuestionRecordsMissingMeasurement()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var fake = new RecordingUnitCommandMeasurer();
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root, measurer: fake);
        Assert.Equal(2, fake.Calls.Count);
        Assert.All(fake.Calls, call => Assert.Equal(UnitCommandKinds.Build, call.Kind));
        Assert.All(model.Measurements, unit => Assert.Null(unit.TestSeconds));
        var unknown = model.Commands.Single(unit => unit.TestCommand is not null);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"commands/{unknown.UnitId}/test/duration");
    }

    [Theory]
    [InlineData(UnitCommandKinds.Build, 3)]
    [InlineData(UnitCommandKinds.Test, 2)]
    public void RequestedKindsRestrictExecution(UnitCommandKinds kind, int calls)
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var fake = new RecordingUnitCommandMeasurer();
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root, measurer: fake, measuredKinds: kind);
        Assert.Equal(calls, fake.Calls.Count);
        Assert.All(fake.Calls, call => Assert.Equal(kind, call.Kind));
        Assert.All(model.Measurements, unit => Assert.Null(kind == UnitCommandKinds.Build ? unit.TestSeconds : unit.BuildSeconds));
    }

    private static void AssertDuration(ProjectFact<double> fact, double expected, string unitId, string kind)
    {
        Assert.Equal(expected, fact.Value);
        Assert.Equal(FactConfidence.High, fact.Confidence);
        Assert.Null(fact.Source.Path);
        Assert.Null(fact.Source.Line);
        Assert.Equal($"measurement:project-discover/{unitId}/{kind}", fact.Source.MeasurementReference);
    }
}
