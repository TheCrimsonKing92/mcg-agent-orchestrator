using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Overlays live owner facts onto read-time settings without changing the gate.</summary>
public static class OwnerAnsweredEngineSettings
{
    public static LearnedEngineSettings Overlay(LearnedEngineSettings learned, AppliedOwnerAnswers applied)
    {
        ArgumentNullException.ThrowIfNull(learned);
        ArgumentNullException.ThrowIfNull(applied);
        var result = learned;
        var answeredKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var answer in applied.AnsweredFacts)
        {
            switch (answer.FactKey)
            {
                case OwnerAnswerValidator.DefaultTimeoutKey when learned.DefaultTimeoutMinutes is null:
                    result = result with { DefaultTimeoutMinutes = Minutes(answer), DefaultTimeoutReason = answer.Reason };
                    break;
                case OwnerAnswerValidator.BuildServerShutdownKey:
                    result = result with { BuildServerShutdownMinutes = Minutes(answer), BuildServerShutdownReason = answer.Reason };
                    break;
                case OwnerAnswerValidator.DiscoveryKey:
                    result = result with { DiscoveryMinutes = Minutes(answer), DiscoveryReason = answer.Reason };
                    break;
                default:
                    continue;
            }
            answeredKeys.Add(answer.FactKey);
        }
        var checkTimeouts = OwnerAnsweredCheckTimeouts.Read(applied);
        if (checkTimeouts.Count > 0)
            result = result with { CheckTimeouts = checkTimeouts };
        return answeredKeys.Count == 0 ? result : result with
        {
            OwnerQuestions = learned.OwnerQuestions.Where(question => !answeredKeys.Contains(question.FactKey)).ToArray()
        };
    }

    private static int Minutes(AppliedOwnerAnswers.AnsweredFact answer) =>
        int.Parse(OwnerAnswerValidator.Validate(answer.FactKey, answer.Fact.Value), CultureInfo.InvariantCulture);
}
