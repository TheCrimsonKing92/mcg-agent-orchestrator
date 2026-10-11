using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Validates owner input against live questions without changing the snapshot.</summary>
public static class OwnerAnswerApplier
{
    public const string NotLiveReason = "the model no longer asks or derives this key";

    public static AppliedOwnerAnswers Apply(ProjectModel model, IReadOnlyList<OwnerAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(answers);
        var liveKeys = model.OwnerQuestions.Concat(LearnedEngineSettingsDeriver.Derive(model).OwnerQuestions)
            .Select(question => question.FactKey).ToHashSet(StringComparer.Ordinal);
        var facts = new List<AppliedOwnerAnswers.AnsweredFact>();
        var stale = new List<AppliedOwnerAnswers.StaleAnswer>();
        foreach (var answer in answers)
        {
            string value;
            try
            {
                value = OwnerAnswerValidator.Validate(answer.FactKey, answer.Value);
            }
            catch (ArgumentException exception)
            {
                stale.Add(new(answer, exception.Message));
                continue;
            }
            if (!liveKeys.Contains(answer.FactKey))
            {
                stale.Add(new(answer, NotLiveReason));
                continue;
            }
            facts.Add(new(answer.FactKey, new ProjectFact<string>(value,
                new FactSource(measurementReference: $"owner-answer:{answer.FactKey}"), FactConfidence.High),
                FormatRecorded(answer.RecordedUtc)));
        }

        var answeredKeys = facts.Select(fact => fact.FactKey).ToHashSet(StringComparer.Ordinal);
        return new(model.OwnerQuestions.Where(question => !answeredKeys.Contains(question.FactKey)).ToArray(),
            facts.AsReadOnly(), stale.AsReadOnly());
    }

    public static string FormatRecorded(DateTimeOffset recordedUtc) =>
        "owner answer recorded " + recordedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
