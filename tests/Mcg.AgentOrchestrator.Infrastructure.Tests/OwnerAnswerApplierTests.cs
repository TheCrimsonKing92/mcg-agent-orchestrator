using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all models and answer lists are invocation-owned in-memory data.
public sealed class OwnerAnswerApplierTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 10, 11, 12, 34, 56, TimeSpan.Zero);
    private const string Reason = "owner answer recorded 2026-10-11T12:34:56Z";

    [Fact]
    public void Apply_LiveQuestion_ReturnsHighFactAndPreservesInputAndQuestionOrder()
    {
        ProjectOwnerQuestion[] questions = [Question("first"), Question("environment/dotnet-sdk"), Question("last")];
        var model = Model(false) with { OwnerQuestions = questions };
        OwnerAnswer[] answers = [new("environment/dotnet-sdk", "10.0.100", "owner explanation", Recorded)];
        var before = ProjectModelJson.Serialize(model);
        var answerBefore = answers.ToArray();

        var applied = OwnerAnswerApplier.Apply(model, answers);

        Assert.Equal(new[] { questions[0], questions[2] }, applied.OpenQuestions);
        var fact = Assert.Single(applied.AnsweredFacts);
        Assert.Equal("environment/dotnet-sdk", fact.FactKey);
        Assert.Equal("10.0.100", fact.Fact.Value);
        Assert.Equal(FactConfidence.High, fact.Fact.Confidence);
        Assert.Equal("owner-answer:environment/dotnet-sdk", fact.Fact.Source.MeasurementReference);
        Assert.Null(fact.Fact.Source.Path);
        Assert.Null(fact.Fact.Source.Line);
        Assert.Equal(Reason, fact.Reason);
        Assert.Empty(applied.StaleAnswers);
        Assert.Same(questions, model.OwnerQuestions);
        Assert.Same(answers[0], answerBefore[0]);
        Assert.Equal(answerBefore, answers);
        Assert.Equal(before, ProjectModelJson.Serialize(model));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_DerivedDiscoveryKey_IsLiveForEveryModel(bool measured)
    {
        var applied = OwnerAnswerApplier.Apply(Model(measured),
            [new("engine/timeouts/discoveryMinutes", "003", null, Recorded)]);
        Assert.Equal("3", Assert.Single(applied.AnsweredFacts).Fact.Value);
        Assert.Empty(applied.StaleAnswers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_DefaultTimeout_IsLiveOnlyWhenDurationIsUnmeasured(bool measured)
    {
        var answer = new OwnerAnswer("engine/timeouts/defaultMinutes", "20", null, Recorded);
        var model = Model(measured);
        Assert.Equal(!measured, LearnedEngineSettingsDeriver.Derive(model).OwnerQuestions
            .Any(question => question.FactKey == answer.FactKey));
        var applied = OwnerAnswerApplier.Apply(model, [answer]);
        if (measured)
        {
            Assert.Empty(applied.AnsweredFacts);
            Assert.Equal(answer, Assert.Single(applied.StaleAnswers).Answer);
            Assert.Equal("the model no longer asks or derives this key", applied.StaleAnswers[0].Reason);
        }
        else
        {
            Assert.Equal("20", Assert.Single(applied.AnsweredFacts).Fact.Value);
            Assert.Empty(applied.StaleAnswers);
        }
    }

    [Fact]
    public void Apply_KeyAbsentFromBothQuestionSets_IsStaleOnly()
    {
        var model = Model(false) with { OwnerQuestions = [Question("unanswered")] };
        var answer = new OwnerAnswer("environment/dotnet-sdk", "10.0.100", null, Recorded);
        var applied = OwnerAnswerApplier.Apply(model, [answer]);
        Assert.Equal(model.OwnerQuestions, applied.OpenQuestions);
        Assert.Empty(applied.AnsweredFacts);
        var stale = Assert.Single(applied.StaleAnswers);
        Assert.Same(answer, stale.Answer);
        Assert.Equal("the model no longer asks or derives this key", stale.Reason);
    }

    [Fact]
    public void Apply_InvalidStoredValue_ReportsValidationAndKeepsQuestionOpen()
    {
        var model = Model(false) with { OwnerQuestions = [Question("environment/dotnet-sdk")] };
        var answer = new OwnerAnswer("environment/dotnet-sdk", "latest", null, Recorded);
        var validation = Assert.Throws<ArgumentException>(() =>
            OwnerAnswerValidator.Validate(answer.FactKey, answer.Value));
        var applied = OwnerAnswerApplier.Apply(model, [answer]);
        Assert.Equal(model.OwnerQuestions, applied.OpenQuestions);
        Assert.Empty(applied.AnsweredFacts);
        var stale = Assert.Single(applied.StaleAnswers);
        Assert.Equal(answer, stale.Answer);
        Assert.Equal(validation.Message, stale.Reason);
    }

    [Theory]
    [InlineData("test", true)]
    [InlineData("production", false)]
    [InlineData("missing", false)]
    [InlineData("undetermined", false)]
    public void Apply_UnitTimeout_IsLiveOnlyForKnownTestUnits(string unit, bool live)
    {
        var model = Model(false) with
        {
            Units = [new("test", Fact("test"), Fact("test.csproj"), Fact<bool?>(true)),
                new("production", Fact("production"), Fact("production.csproj"), Fact<bool?>(false)),
                new("undetermined", Fact("undetermined"), Fact("undetermined.csproj"), Fact<bool?>(null))]
        };
        var answer = new OwnerAnswer($"commands/{unit}/timeoutMinutes", "025", null, Recorded);

        var applied = OwnerAnswerApplier.Apply(model, [answer]);

        if (live)
        {
            var fact = Assert.Single(applied.AnsweredFacts);
            Assert.Equal(answer.FactKey, fact.FactKey);
            Assert.Equal("25", fact.Fact.Value);
            Assert.Empty(applied.StaleAnswers);
        }
        else
        {
            Assert.Empty(applied.AnsweredFacts);
            var stale = Assert.Single(applied.StaleAnswers);
            Assert.Same(answer, stale.Answer);
            Assert.Equal(OwnerAnswerApplier.NotLiveReason, stale.Reason);
        }
    }

    private static ProjectOwnerQuestion Question(string key) => new(key, "question " + key, Source());
    private static FactSource Source() => new(measurementReference: "fixture");
    private static ProjectFact<T> Fact<T>(T value) => new(value, Source(), FactConfidence.High);
    private static ProjectModel Model(bool measured) => new(ProjectModel.CurrentSchemaVersion, ".",
        [new("test", Fact("test"), Fact("test.csproj"), Fact<bool?>(true))], [], [], [],
        [new("test", Fact("build"), Fact("test"))], [],
        measured ? [new("test", null, Fact(200d))] : [], []);
}
