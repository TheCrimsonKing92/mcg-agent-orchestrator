using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Recognizes valid negative-only expressions, never a general failure to extract positive clauses.
internal static class PreReviewExclusionOnlyFilter
{
    internal const string NotApplicableReason =
        "exclusion-only lane filter; the acceptance gate runs the full lane";

    private static readonly Regex NegativePredicate = new(
        @"\A[A-Za-z_][A-Za-z0-9_]*\s*(?:!~|!=)\s*[A-Za-z_][A-Za-z0-9_.+-]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool IsExclusionOnly(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        var depth = 0;
        var expectsOperand = true;
        for (var index = 0; index < filter.Length; index++)
        {
            var token = filter[index];
            if (char.IsWhiteSpace(token))
            {
                continue;
            }

            switch (token)
            {
                case '(':
                    if (!expectsOperand)
                    {
                        return false;
                    }
                    depth++;
                    break;
                case ')':
                    if (expectsOperand || depth == 0)
                    {
                        return false;
                    }
                    depth--;
                    break;
                case '&':
                case '|':
                    if (expectsOperand)
                    {
                        return false;
                    }
                    expectsOperand = true;
                    break;
                default:
                    if (!expectsOperand)
                    {
                        return false;
                    }
                    var start = index;
                    while (index < filter.Length && filter[index] is not ('(' or ')' or '&' or '|'))
                    {
                        index++;
                    }
                    if (!NegativePredicate.IsMatch(filter[start..index].Trim()))
                    {
                        return false;
                    }
                    index--;
                    expectsOperand = false;
                    break;
            }
        }

        return !expectsOperand && depth == 0;
    }
}
