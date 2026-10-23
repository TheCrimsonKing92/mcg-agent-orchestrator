using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Credit is evidence about this attempt's named failures, never about an inferred singleton goal.
internal static class AcceptanceProbeRerunCredit
{
    internal sealed record Decision(
        string? DenialReason, IReadOnlyList<string> Identities, IReadOnlyList<string> HistoryGoals,
        AcceptanceCheckResult? Confirmation = null)
    {
        internal bool Granted => DenialReason is null && Confirmation is not null;
    }

    internal static bool IsEligiblePredicate(string? predicate) => predicate is
        AcceptanceShardCompletionPredicates.IncompleteExecution or AcceptanceShardCompletionPredicates.NonzeroExit;

    internal static async Task<Decision> EvaluateAsync(
        AcceptanceManifestCheck check, AcceptanceCheckResult first, AcceptanceCheckResult probe,
        string goalId, IReadOnlyList<string>? members,
        Func<DateTimeOffset, IReadOnlyList<AcceptanceLaneFlakeRow>> readHistory,
        Func<IReadOnlyList<string>> changedPaths, string worktreePath,
        Func<AcceptanceManifestCheck, Task<AcceptanceCheckResult>> confirm,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var identities = (first.FailingTestIdentities ?? [])
            .Concat(RecoverTrxFailures(first.TestResultPaths))
            .Distinct(StringComparer.Ordinal).ToArray();
        IReadOnlyList<string> historyGoals = [];
        Decision Deny(string reason) => new(reason, identities, historyGoals);

        // 1. A successful full probe must have adjudicated execution evidence.
        if (first.Passed || first.CompletionDecision is not { Passed: false } ||
            !IsEligiblePredicate(first.CompletionDecision.FailedPredicate) || identities.Length == 0 ||
            !probe.Passed || probe.CompletionDecision is not { Passed: true, FailedPredicate: null } ||
            (probe.CompletionDecision.ExecutedTestCount ?? probe.ExecutedTestCount) is not > 0 ||
            probe.TestResultPaths is not { Count: > 0 } || probe.FailingTestIdentities is { Count: > 0 })
            return Deny("not-eligible");

        // 2. Only the caller can establish complete attempt membership.
        if (members is not { Count: > 0 } || members.Any(string.IsNullOrWhiteSpace) ||
            !members.Contains(goalId, StringComparer.Ordinal))
            return Deny("members-unknown");

        // 3. Every complete identity needs recent evidence from outside every member.
        AcceptanceLaneFlakeRow[] history;
        try
        {
            history = readHistory(now.AddDays(-14)).Where(row =>
                row.RecordedAt >= now.AddDays(-14) && row.RecordedAt <= now &&
                !string.IsNullOrWhiteSpace(row.GoalId) && !members.Contains(row.GoalId, StringComparer.Ordinal) &&
                row.FirstFailingTestIdentities.Any(identity => identities.Contains(identity, StringComparer.Ordinal)))
                .ToArray();
        }
        catch (Exception) { return Deny("no-cross-goal-history"); }
        if (identities.Any(identity => !history.Any(row => row.FirstFailingTestIdentities.Contains(identity, StringComparer.Ordinal))))
            return Deny("no-cross-goal-history");
        historyGoals = history.Select(row => row.GoalId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        // 4. Theory arguments affect history, but not the declaring class.
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            var selector = AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
            var separator = selector.LastIndexOf('.');
            if (separator <= 0 || separator == selector.Length - 1)
                return Deny("identity-unresolved");
            var qualifiedClass = selector[..separator];
            if (qualifiedClass.Split('.', '+').Any(part => !Regex.IsMatch(part, @"^[_\p{L}][_\p{L}\p{Nd}]*$")))
                return Deny("identity-unresolved");
            foreach (var className in qualifiedClass[(qualifiedClass.LastIndexOf('.') + 1)..].Split('+'))
                classes.Add(className);
        }

        // 5. Removed files still count by stem; declarations use candidate worktree content.
        try
        {
            foreach (var path in changedPaths())
            {
                if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (classes.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase))
                    return Deny("inside-diff");
                var candidatePath = Path.Combine(worktreePath, path);
                string content;
                try { content = File.ReadAllText(candidatePath); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if (classes.Any(name => Regex.IsMatch(content, @"\bclass\s+@?" + Regex.Escape(name) + @"\b")))
                    return Deny("inside-diff");
            }
        }
        catch (Exception) { return Deny("inside-diff"); }

        // 6. The existing narrowing contract rejects unsafe/no-op selections before execution.
        var plan = AcceptanceLaneRerunNarrowing.Select(check, identities);
        if (!plan.IsNarrowed || plan.Tokens.Count == 0) return Deny("confirm-red");
        AcceptanceCheckResult confirmation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            confirmation = await confirm(AcceptanceLaneRerunNarrowing.BuildRerunCheck(check, plan)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception) { return Deny("confirm-red"); }
        return AcceptanceLaneRerunNarrowing.Passed(plan, confirmation)
            ? new(null, identities, historyGoals, confirmation) : Deny("confirm-red");
    }

    private static IReadOnlyList<string> RecoverTrxFailures(IEnumerable<string>? paths)
    {
        var identities = new List<string>();
        foreach (var path in paths ?? [])
        {
            try
            {
                var document = XDocument.Load(path, LoadOptions.None);
                var definitions = document.Descendants().Where(element =>
                    element.Name.LocalName == "UnitTest" && !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
                    .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult" &&
                    (string.Equals(element.Attribute("outcome")?.Value, "Failed", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(element.Attribute("outcome")?.Value, "Timeout", StringComparison.OrdinalIgnoreCase))))
                {
                    definitions.TryGetValue(result.Attribute("testId")?.Value ?? string.Empty, out var definition);
                    identities.Add(AcceptanceTrxTestIdentityResolver.Resolve(result, definition) ??
                        result.Attribute("testId")?.Value?.Trim() ?? "unknown test");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // Retain reported failures when a first-run TRX is missing or unreadable.
            }
        }
        return identities;
    }
}
