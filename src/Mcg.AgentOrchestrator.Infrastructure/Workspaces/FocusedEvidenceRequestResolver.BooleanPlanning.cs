using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class FocusedEvidenceRequestResolver
{
    private static bool IsPositiveToken(FocusedEvidenceFilterToken token) =>
        token.Kind is FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method;

    // An arm is one conjunction of MTP option groups. Repeated positives of the same
    // kind denote OR; different groups and all exclusions denote AND.
    private static string CanonicalLaunchFilter(IReadOnlyList<FocusedEvidenceFilterToken> arm)
    {
        var clauses = arm.Where(IsPositiveToken).GroupBy(token => token.Kind)
            .Select(group => string.Join("|", group.Select(token => token.CanonicalToken)))
            .Concat(arm.Where(token => !IsPositiveToken(token)).Select(token => token.CanonicalToken))
            .ToArray();
        return clauses.Length == 1 ? clauses[0] :
            string.Join("&", clauses.Select(clause => clause.Contains('|') ? $"({clause})" : clause));
    }

    internal static void ValidateFocusedLaunch(string expression, IReadOnlyList<FocusedEvidenceFilterToken> tokens)
    {
        try
        {
            var arms = PlanBooleanFilter(expression, original =>
                tokens.FirstOrDefault(token => token.CanonicalToken.Equals(original.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new FocusedBooleanException(original, "filter and resolved tokens differ"));
            if (arms.Count != 1 || !arms[0].Any(IsPositiveToken) ||
                !arms[0].Select(token => token.CanonicalToken).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(tokens.Select(token => token.CanonicalToken)))
                throw new FocusedBooleanException(expression, "filter requires separate launches or has inconsistent tokens");
        }
        catch (FocusedBooleanException ex)
        {
            throw new InvalidOperationException($"Unsupported focused evidence launch: {ex.Rejection.Detail}", ex);
        }
    }

    private static IReadOnlyList<IReadOnlyList<FocusedEvidenceFilterToken>> PlanBooleanFilter(
        string expression, Func<string, FocusedEvidenceFilterToken> resolve)
    {
        var index = 0;
        var result = ParseOr();
        SkipWhitespace();
        if (index != expression.Length)
            throw Invalid("unexpected closing parenthesis or operand");
        return result;

        List<IReadOnlyList<FocusedEvidenceFilterToken>> ParseOr()
        {
            var left = ParseAnd();
            while (Take('|') || Take(','))
            {
                var right = ParseAnd();
                if (left.Concat(right).Any(arm => !arm.Any(IsPositiveToken)))
                    throw Invalid("negative alternatives under OR are unsupported");
                var all = left.Concat(right).ToArray();
                // Only a homogeneous positive union is representable as one MTP group.
                if (all.SelectMany(arm => arm).All(IsPositiveToken) &&
                    all.SelectMany(arm => arm).Select(token => token.Kind).Distinct().Count() == 1)
                    left = [all.SelectMany(arm => arm).DistinctBy(token => token.CanonicalToken).ToArray()];
                else
                    left.AddRange(right);
            }
            return left;
        }

        List<IReadOnlyList<FocusedEvidenceFilterToken>> ParseAnd()
        {
            var left = ParsePrimary();
            while (Take('&'))
            {
                var right = ParsePrimary();
                // Allow shared exclusions over unions, but never expand a product of
                // positive unions into an unbounded set of launches.
                if ((left.Count > 1 && right.Any(arm => arm.Any(IsPositiveToken))) ||
                    (right.Count > 1 && left.Any(arm => arm.Any(IsPositiveToken))))
                    throw Invalid("conjunction of partitioned positive alternatives is unsupported");
                var combined = new List<IReadOnlyList<FocusedEvidenceFilterToken>>();
                foreach (var a in left)
                foreach (var b in right)
                {
                    if (a.Where(IsPositiveToken).Select(token => token.Kind)
                        .Intersect(b.Where(IsPositiveToken).Select(token => token.Kind)).Any())
                        throw Invalid("same-kind positive intersections are unsupported");
                    combined.Add(a.Concat(b).DistinctBy(token => token.CanonicalToken).ToArray());
                }
                left = combined;
            }
            return left;
        }

        List<IReadOnlyList<FocusedEvidenceFilterToken>> ParsePrimary()
        {
            if (Take('('))
            {
                var nested = ParseOr();
                if (!Take(')')) throw Invalid("unclosed parenthesis");
                return nested;
            }
            var start = index;
            while (index < expression.Length && expression[index] is not ('(' or ')' or '&' or '|' or ','))
                index++;
            var atom = expression[start..index];
            if (string.IsNullOrWhiteSpace(atom)) throw Invalid("empty operand");
            return [[resolve(atom)]];
        }

        bool Take(char value)
        {
            var start = index;
            SkipWhitespace();
            if (index == expression.Length || expression[index] != value)
            {
                index = start;
                return false;
            }
            index++;
            return true;
        }

        void SkipWhitespace()
        {
            while (index < expression.Length && char.IsWhiteSpace(expression[index])) index++;
        }

        FocusedBooleanException Invalid(string reason) => new(expression, reason);
    }

    private sealed class FocusedBooleanException : Exception
    {
        internal FocusedEvidenceRejection Rejection { get; }

        internal FocusedBooleanException(string token, string reason) : this(new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.UnsupportedToken, token, $"focused evidence filter is unsupported: {reason}")) { }

        internal FocusedBooleanException(FocusedEvidenceRejection rejection) => Rejection = rejection;
    }
}
