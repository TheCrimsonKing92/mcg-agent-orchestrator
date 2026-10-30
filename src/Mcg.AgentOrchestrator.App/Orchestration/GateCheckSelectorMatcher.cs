using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateCheckSelectorMatcher
{
    internal enum Outcome { NotApplicable, Satisfied, Failed, Pending }

    internal static string Normalize(string? value)
    {
        var text = value?.Trim() ?? "";
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return text[..end] is "npx" or "pnpm" or "yarn" or "bunx"
            ? text[end..].Trim() : text;
    }

    internal static string? CommandLine(AcceptanceManifestCheck check) =>
        string.IsNullOrWhiteSpace(check.Command) ? null : string.Join(" ", new[] { check.Command }.Concat(check.Arguments));

    internal static bool Matches(string selector, AcceptanceManifestCheck check)
    {
        var normalized = Normalize(selector);
        return normalized.Length > 0 &&
            (string.Equals(normalized, Normalize(check.Name), StringComparison.Ordinal) ||
             string.Equals(normalized, Normalize(CommandLine(check)), StringComparison.Ordinal));
    }

    internal static bool IsTestResultProducing(AcceptanceManifestCheck check) =>
        string.Equals(check.Type, "dotnet-test", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(check.Type, "crosswalk-test", StringComparison.OrdinalIgnoreCase) ||
        GoPackageFocusedEvidence.IsGoTestCommand(check);

    internal static Outcome Grade(string selector, ProspectiveEvidenceGateResolver.GateCheckOutcomes? outcomes)
    {
        if (outcomes is null) return Outcome.NotApplicable;
        var checks = outcomes.Declared.Where(check => Matches(selector, check)).ToArray();
        // A test declaration keeps its existing row-based grading, even without artifacts.
        if (checks.Length == 0 || checks.Any(IsTestResultProducing)) return Outcome.NotApplicable;
        var pending = false;
        foreach (var check in checks)
        {
            if (outcomes.Explicit.TryGetValue(check.Name, out var passed))
            {
                if (!passed) return Outcome.Failed;
            }
            else if (!outcomes.PassedWithNoFailedChecks) pending = true;
        }
        return pending ? Outcome.Pending : Outcome.Satisfied;
    }
}
