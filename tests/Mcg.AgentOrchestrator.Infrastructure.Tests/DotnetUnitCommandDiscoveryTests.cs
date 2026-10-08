using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test reads or edits its isolated fixture copy.
public sealed class DotnetUnitCommandDiscoveryTests
{
    [Fact]
    public void CommandsAreExactAndSourcesIdentifyDeclarations()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal(model.Units.Select(unit => unit.Id), model.Commands.Select(unit => unit.UnitId));
        string[] expectedBuilds = [
            "dotnet build src/Sample.Library/Sample.Library.csproj",
            "dotnet build tests/Sample.MtpTests/Sample.MtpTests.csproj",
            "dotnet build tests/Sample.VstestTests/Sample.VstestTests.csproj"
        ];
        Assert.Equal(expectedBuilds, model.Commands.Select(unit => unit.BuildCommand.Value));
        Assert.Null(model.Commands[0].TestCommand);
        Assert.Equal("dotnet run --project tests/Sample.MtpTests/Sample.MtpTests.csproj --no-build --property:UseAppHost=false",
            model.Commands[1].TestCommand!.Value);
        Assert.Equal("dotnet test tests/Sample.VstestTests/Sample.VstestTests.csproj --no-build", model.Commands[2].TestCommand!.Value);
        Assert.NotEqual(model.Commands[1].TestCommand!.Value, model.Commands[2].TestCommand!.Value);
        foreach (var unit in model.Commands)
        {
            Assert.Equal(FactConfidence.High, unit.BuildCommand.Confidence);
            Assert.Contains("Sdk=", SourceLine(fixture.Root, unit.BuildCommand.Source));
            Assert.Equal(unit.UnitId, unit.BuildCommand.Source.Path);
            if (unit.TestCommand is null) continue;
            var status = model.Units.Single(item => item.Id == unit.UnitId).IsTest;
            var runner = model.TestSetups.Single(item => item.UnitId == unit.UnitId).Runner;
            Assert.True(unit.TestCommand.Confidence >= status.Confidence);
            Assert.True(unit.TestCommand.Confidence >= runner.Confidence);
            Assert.Equal(runner.Source, unit.TestCommand.Source);
            Assert.Contains(runner.Value == "MTP" ? "UseMicrosoftTestingPlatformRunner" : "Microsoft.NET.Test.Sdk",
                SourceLine(fixture.Root, unit.TestCommand.Source));
        }
        Assert.Empty(model.OwnerQuestions);
    }

    [Fact]
    public void UndeterminedRunnerNeverProducesGuessedCommand()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var unit = model.Commands.Single(item => item.TestCommand is not null);
        Assert.Equal("undetermined", unit.TestCommand!.Value);
        Assert.Equal(FactConfidence.Low, unit.TestCommand.Confidence);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"commands/{unit.UnitId}/test");
        SourceLine(fixture.Root, unit.TestCommand.Source);
    }

    [Fact]
    public void MalformedProjectAndUncertainTestStatusProduceCommandQuestions()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        const string id = "tests/Sample.VstestTests/Sample.VstestTests.csproj";
        File.WriteAllText(Path.Combine(fixture.Root, id), "<Project>");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var unit = model.Commands.Single(item => item.UnitId == id);
        Assert.Equal(FactConfidence.Low, unit.BuildCommand.Confidence);
        Assert.Equal("undetermined", unit.TestCommand!.Value);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"commands/{id}/build");
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"commands/{id}/test");
    }

    [Fact]
    public void WhitespaceProjectPathsAreQuoted()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        const string id = "Space Unit.csproj";
        File.WriteAllText(Path.Combine(fixture.Root, id), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup></Project>");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal("dotnet build \"Space Unit.csproj\"", model.Commands.Single(item => item.UnitId == id).BuildCommand.Value);
    }

    private static string SourceLine(string root, FactSource source)
    {
        Assert.NotNull(source.Path);
        Assert.Null(source.MeasurementReference);
        var lines = File.ReadAllLines(Path.Combine(root, source.Path));
        Assert.InRange(source.Line!.Value, 1, lines.Length);
        return lines[source.Line.Value - 1];
    }
}
