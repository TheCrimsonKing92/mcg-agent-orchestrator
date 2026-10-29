using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Enriches the durable cohort attribution; missing messages never change routing eligibility.
internal static class AttributedCohortFailureDetail
{
    internal static string Format(IReadOnlyList<string> reproduced, IReadOnlyList<string> testResultPaths)
    {
        var names = reproduced.ToHashSet(StringComparer.Ordinal);
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in testResultPaths)
        {
            var result = AcceptanceTrxFailureReader.Read(path);
            if (result.Status != AcceptanceTrxReadStatus.Readable) continue;
            foreach (var failure in result.Failures)
            {
                if (failure.TestName is not { } name || !names.Contains(name)) continue;
                var line = failure.Message?.Split(['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(line))
                    messages.TryAdd(name, MergeTrainAttributedFailure.BoundMessage(line));
            }
        }

        return string.Concat(messages.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"\nreproduced {pair.Key}: {pair.Value}"));
    }

    // Comma-delimited evidence cannot safely carry split theory fragments or line breaks.
    // Spaces are representable inside one complete trailing data-case suffix only.
    internal static bool IsRepresentableTestName(string name) =>
        !name.Any(char.IsControl) && Regex.IsMatch(name,
            @"\A[^\s,;()\[\]]+(?:\([^,;()\[\]]*\)|\[[^,;()\[\]]*\])?\z");
}
