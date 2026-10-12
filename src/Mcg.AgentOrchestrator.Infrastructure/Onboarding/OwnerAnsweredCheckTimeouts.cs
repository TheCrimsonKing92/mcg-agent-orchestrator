using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reads live per-unit timeout answers for emission and learned comparison.</summary>
public static class OwnerAnsweredCheckTimeouts
{
    public static IReadOnlyDictionary<string, (int Minutes, string Reason)> Read(AppliedOwnerAnswers applied)
    {
        ArgumentNullException.ThrowIfNull(applied);
        const string prefix = "commands/";
        const string suffix = "/timeoutMinutes";
        var timeouts = new Dictionary<string, (int Minutes, string Reason)>(StringComparer.Ordinal);
        foreach (var answer in applied.AnsweredFacts)
        {
            var key = answer.FactKey;
            if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
                !key.EndsWith(suffix, StringComparison.Ordinal) || key.Length <= prefix.Length + suffix.Length)
                continue;
            var unit = key[prefix.Length..^suffix.Length].Replace('\\', '/');
            var minutes = int.Parse(OwnerAnswerValidator.Validate(key, answer.Fact.Value), CultureInfo.InvariantCulture);
            timeouts[unit] = (minutes, answer.Reason);
        }
        return timeouts;
    }
}
