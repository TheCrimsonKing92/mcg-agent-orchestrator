using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each discovery owns its fixture copy; answers and models are local inputs.
public sealed class OwnerAnswerRunnerRuleTests
{
    private const string Unit = "tests/Loose.UndeterminedTests/Loose.UndeterminedTests.csproj";
    private const string Key = "commands/" + Unit + "/test";
    private static readonly DateTimeOffset Recorded = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("VSTest", "VSTest")]
    [InlineData("vstest", "VSTest")]
    [InlineData("VsTest", "VSTest")]
    [InlineData("MTP", "MTP")]
    [InlineData("mtp", "MTP")]
    public void Validate_AllowedRunner_ReturnsCanonicalSpelling(string value, string expected) =>
        Assert.Equal(expected, OwnerAnswerValidator.Validate("commands/tests/A/A.csproj/test", value));

    [Theory]
    [InlineData("")]
    [InlineData(" MTP")]
    [InlineData("VSTest ")]
    [InlineData("xunit")]
    [InlineData("dotnet test")]
    [InlineData("NUnit")]
    [InlineData("vstest;mtp")]
    public void Validate_InvalidRunner_NamesKeyAndAllowedValues(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(Key, value));
        Assert.Contains(Key, error.Message);
        Assert.Contains("VSTest", error.Message);
        Assert.Contains("MTP", error.Message);
        Assert.DoesNotContain("has no answer rule", error.Message);
    }

    [Theory]
    [InlineData("commands//test")]
    [InlineData("commands/A/test/duration")]
    [InlineData("commands/A/build")]
    [InlineData("tests/A/runner")]
    [InlineData("commands/A/tests")]
    public void Validate_UnmatchedKey_KeepsUnknownKeyRefusal(string key)
    {
        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(key, "VSTest"));
        Assert.Contains(key + " has no answer rule", error.Message);
        Assert.Contains("commands/<unit>/test", error.Message);
    }

    [Fact]
    public void Derive_WithoutAnswers_PreservesUnresolvedUnit()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var result = LearnedTestCheckDeriver.Derive(model);
        Assert.Empty(result.Checks);
        var unresolved = Assert.Single(result.Unresolved);
        Assert.Equal(Unit, unresolved.UnitId);
        Assert.Equal(Key, unresolved.FactKey);
    }

    [Theory]
    [InlineData("VSTest", "vstest")]
    [InlineData("MTP", "mtp")]
    public void Derive_AnsweredRunner_PromotesUnitWithOwnerProvenance(string value, string runner)
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var snapshot = ProjectModelJson.Serialize(model);
        var applied = OwnerAnswerApplier.Apply(model, [Answer(Key, value)]);
        Assert.Empty(applied.StaleAnswers);
        Assert.Contains(applied.OpenQuestions, question => question.FactKey == $"tests/{Unit}/runner");
        var answered = Assert.Single(applied.AnsweredFacts);
        var result = LearnedTestCheckDeriver.Derive(model, applied);
        var check = Assert.Single(result.Checks);
        Assert.Empty(result.Unresolved);
        Assert.Equal(Unit, check.ProjectPath);
        Assert.Equal(runner, check.Runner);
        Assert.Equal(FactConfidence.High, check.Confidence);
        Assert.Equal(answered.Fact.Source, check.Source);
        Assert.Equal("owner-answer:" + Key, check.Source.MeasurementReference);
        Assert.Equal(snapshot, ProjectModelJson.Serialize(model));
        Assert.Empty(LearnedTestCheckDeriver.Derive(model).Checks);
    }

    [Fact]
    public void Derive_DuplicateAnswers_LastEntryWinsWithoutSortingTimes()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var applied = OwnerAnswerApplier.Apply(model,
            [Answer(Key, "VSTest"), new(Key, "MTP", "later choice", Recorded.AddMinutes(1))]);
        Assert.Empty(applied.StaleAnswers);
        Assert.Equal(2, applied.AnsweredFacts.Count);
        var result = LearnedTestCheckDeriver.Derive(model, applied);
        Assert.Equal("mtp", Assert.Single(result.Checks).Runner);
        Assert.Empty(result.Unresolved);
        var reversed = applied with { AnsweredFacts = applied.AnsweredFacts.Reverse().ToArray() };
        Assert.Equal("vstest", Assert.Single(LearnedTestCheckDeriver.Derive(model, reversed).Checks).Runner);
    }

    [Theory]
    [InlineData(FactConfidence.High)]
    [InlineData(FactConfidence.Medium)]
    public void Derive_ConfidentRunner_PreservesLearnedCheck(FactConfidence confidence)
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var setup = model.TestSetups.First();
        var source = new FactSource(measurementReference: "confident discovery");
        model = model with
        {
            TestSetups = model.TestSetups.Select(item => item.UnitId == setup.UnitId
                ? item with { Runner = new ProjectFact<string>("VSTest", source, confidence) } : item).ToArray()
        };
        var applied = new AppliedOwnerAnswers([], [new($"commands/{setup.UnitId}/test",
            new ProjectFact<string>("MTP", new FactSource(measurementReference: "owner control"),
                FactConfidence.High), "control")], []);
        var original = LearnedTestCheckDeriver.Derive(model);
        Assert.NotEmpty(original.Checks);
        var result = LearnedTestCheckDeriver.Derive(model, applied);
        Assert.Equal(original.Checks, result.Checks);
        Assert.Equal(original.Unresolved, result.Unresolved);
        var check = result.Checks.Single(item => item.ProjectPath == setup.UnitId);
        Assert.Equal("vstest", check.Runner);
        Assert.Equal(confidence, check.Confidence);
        Assert.Equal(source, check.Source);
    }

    [Fact]
    public void Build_NoRunnerAnswer_BlocksOnlyRunnerKey()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var result = LearnedAcceptanceManifest.Build(model, TimeoutAnswers());
        Assert.Null(result.Json);
        Assert.Equal(Key, Assert.Single(result.Blockers).FactKey);
    }

    [Fact]
    public void Build_VstestAnswer_EmitsOneCheckIdenticalToConfidentModel()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var result = LearnedAcceptanceManifest.Build(model, [.. TimeoutAnswers(), Answer(Key, "VSTest")]);
        Assert.NotNull(result.Json);
        Assert.Empty(result.Blockers);
        using var document = JsonDocument.Parse(result.Json);
        var check = Assert.Single(document.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal("vstest", check.GetProperty("runner").GetString());
        Assert.Equal(Unit, check.GetProperty("project").GetString());
        var confident = model with
        {
            TestSetups = model.TestSetups.Select(setup => setup.UnitId == Unit ? setup with
            {
                Runner = new ProjectFact<string>("VSTest", new FactSource(measurementReference: "hand-set"),
                    FactConfidence.High)
            } : setup).ToArray()
        };
        var expected = LearnedAcceptanceManifest.Build(confident, TimeoutAnswers());
        Assert.NotNull(expected.Json);
        Assert.Empty(expected.Blockers);
        Assert.Equal(Encoding.UTF8.GetBytes(expected.Json), Encoding.UTF8.GetBytes(result.Json));
    }

    [Fact]
    public void Build_MtpAnswer_EmitsOneInvocationDeclaration()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var result = LearnedAcceptanceManifest.Build(model, [.. TimeoutAnswers(), Answer(Key, "MTP")]);
        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        using var document = JsonDocument.Parse(result.Json);
        Assert.Single(document.RootElement.GetProperty("engine").GetProperty("mtpInvocations").EnumerateArray());
    }

    private static OwnerAnswer Answer(string key, string value) => new(key, value, "owner choice", Recorded);
    private static OwnerAnswer[] TimeoutAnswers() =>
        [Answer(OwnerAnswerValidator.DefaultTimeoutKey, "17"),
            Answer(OwnerAnswerValidator.BuildServerShutdownKey, "3"), Answer(OwnerAnswerValidator.DiscoveryKey, "4")];
}
