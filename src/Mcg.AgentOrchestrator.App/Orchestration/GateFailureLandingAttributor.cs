using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Advisory attribution from the candidate diff and landing history, never a gate verdict.</summary>
internal sealed class GateFailureLandingAttributor
{
    internal enum Origin { Own, Interaction, Unattributed }
    internal sealed record ClassAttribution(string ClassName, Origin Origin, string Line);

    private readonly string _repositoryRoot;
    private readonly Func<string, IReadOnlyList<string>, GitCli.GitResult> _gitRunner;

    internal GateFailureLandingAttributor(
        string repositoryRoot,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null)
    {
        _repositoryRoot = repositoryRoot;
        _gitRunner = gitRunner ?? ((root, args) => GitCli.Run(root, 10_000, args.ToArray()));
    }

    internal static string? SelectLastPassingCandidateSha(Goal goal) => goal.Tasks
        .Where(task => task.Status == WorkTaskStatus.Completed &&
            task.RequiredRole is AgentRole.Tester or AgentRole.Reviewer &&
            task.LastVerification is { Succeeded: true } verification &&
            !string.IsNullOrWhiteSpace(verification.ReviewedCommit))
        .OrderByDescending(task => task.LastVerification!.CompletedAt)
        .Select(task => task.LastVerification!.ReviewedCommit!.Trim())
        .FirstOrDefault();

    internal IReadOnlyList<ClassAttribution> Attribute(
        Goal goal,
        string? mainHeadSha,
        ApparatusRedGateReading reading,
        IReadOnlyList<AcceptanceCheckResult> actionableCriteria)
    {
        try
        {
            var identities = actionableCriteria.SelectMany(check => check.FailingTestIdentities ?? [])
                .ToHashSet(StringComparer.Ordinal);
            var classes = reading.FailingTests.Where(test => identities.Contains(test.TestIdentity))
                .GroupBy(test => ClassName(test.TestIdentity), StringComparer.Ordinal)
                .Where(group => group.Any(test => test.ResolvedSourcePaths.Count > 0))
                .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
            var changedPaths = reading.ChangedPaths.Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var anchor = SelectLastPassingCandidateSha(goal);
            string? historyError = anchor is not null && string.IsNullOrWhiteSpace(mainHeadSha)
                ? "main head unavailable" : null;
            var result = new List<ClassAttribution>();
            foreach (var group in classes.Take(10))
            {
                ClassAttribution attribution;
                try
                {
                    var paths = group.SelectMany(test => test.ResolvedSourcePaths).Select(NormalizePath)
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    var types = group.SelectMany(test => test.ReferencedChangedTypes ?? [])
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    var ownPath = paths.FirstOrDefault(changedPaths.Contains);
                    if (ownPath is not null)
                    {
                        attribution = new(group.Key, Origin.Own,
                            $"{group.Key}: test source {ownPath} changed in this candidate's diff; likely this candidate's own failure.");
                    }
                    else
                    {
                        var landings = new List<(string Sha, string Goal)>();
                        var added = false;
                        if (anchor is not null && historyError is null)
                        {
                            var range = $"{anchor}..{mainHeadSha}";
                            var log = ReadLog(["log", "--format=%H%x1f%(trailers:key=Goal,valueonly,separator=)", range, "--", .. paths], ref historyError);
                            if (historyError is null)
                            {
                                foreach (var row in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var fields = row.Trim().Split('\u001f', 2);
                                    var landingGoal = NormalizeGoal(fields.Length > 1 ? fields[1].Trim() : "");
                                    if (landingGoal != NormalizeGoal(goal.Id.Value))
                                        landings.Add((fields[0].Trim(), landingGoal));
                                }
                                if (landings.Count > 0)
                                    added = !string.IsNullOrWhiteSpace(ReadLog(
                                        ["log", "--diff-filter=A", "--format=%H", range, "--", .. paths], ref historyError));
                            }
                        }

                        // A known anchor with unreadable history cannot support a claim of no landing.
                        if (historyError is not null)
                            attribution = Unreadable(group.Key, historyError);
                        else if (landings.Count > 0)
                        {
                            var newest = landings[0];
                            var line = $"{group.Key}: {(added ? "added" : "modified")} by goal {Short(newest.Goal, 8)} (commit {Short(newest.Sha, 9)}) on main after this candidate's last pass at {Short(anchor!, 8)}; likely interaction with that landing.";
                            var others = landings.Select(landing => landing.Goal).Distinct(StringComparer.Ordinal).Count() - 1;
                            if (others > 0) line += $" (+{others} other landing goals)";
                            if (types.Length > 0) line += $" This candidate changed referenced type(s) {string.Join(", ", types)}.";
                            attribution = new(group.Key, Origin.Interaction, line);
                        }
                        else if (types.Length > 0)
                        {
                            var since = anchor is null ? "the last pass (no anchor)" : $"this candidate's last pass at {Short(anchor, 8)}";
                            attribution = new(group.Key, Origin.Own,
                                $"{group.Key}: references candidate-changed type(s) {string.Join(", ", types)}; no landing touched its test source since {since}; likely this candidate's own failure.");
                        }
                        else if (anchor is null)
                            attribution = new(group.Key, Origin.Unattributed,
                                $"{group.Key}: no passing Tester or Reviewer verification records a candidate sha, so main history was not read; unattributed.");
                        else
                            attribution = new(group.Key, Origin.Unattributed,
                                $"{group.Key}: not changed by this candidate and not touched on main since this candidate's last pass at {Short(anchor, 8)}; unattributed.");
                    }
                }
                catch (Exception ex)
                {
                    historyError ??= FirstLine(ex.Message);
                    attribution = Unreadable(group.Key, historyError);
                }

                var firstIdentity = group.Select(test => test.TestIdentity).Order(StringComparer.Ordinal).First();
                var evidence = actionableCriteria.SelectMany(check => check.FailingTestAttributions ?? [])
                    .FirstOrDefault(entry => entry.TestIdentity.Equals(firstIdentity, StringComparison.Ordinal))?.Evidence;
                var lineWithEvidence = attribution.Line;
                if (!string.IsNullOrWhiteSpace(evidence))
                    lineWithEvidence += $" Merge-base arm: {evidence.TrimEnd().TrimEnd('.').TrimEnd()}.";
                result.Add(attribution with { Line = lineWithEvidence.Length > 400 ? lineWithEvidence[..397] + "..." : lineWithEvidence });
            }
            if (classes.Length > 10)
                result.Add(new("", Origin.Unattributed, $"{classes.Length - 10} more failing classes not attributed."));
            return result;
        }
        catch (Exception)
        {
            // Advisory formatting must never interrupt the existing retry/escalation route.
            return [];
        }
    }

    private string ReadLog(IReadOnlyList<string> args, ref string? error)
    {
        try
        {
            var result = _gitRunner(_repositoryRoot, args);
            if (!result.Succeeded || result.DrainTimedOut || !result.ProcessStarted)
            {
                var reason = result.DrainTimedOut ? "timeout" : !result.ProcessStarted ? "process not started" : $"exit {result.ExitCode}";
                var detail = FirstLine(result.Error);
                error = reason + (detail.Length == 0 ? "" : ": " + detail);
                return "";
            }
            return result.Output;
        }
        catch (Exception ex)
        {
            error = FirstLine(ex.Message);
            return "";
        }
    }

    private static ClassAttribution Unreadable(string name, string reason) =>
        new(name, Origin.Unattributed, $"{name}: main history unreadable ({reason}); unattributed.");

    private static string ClassName(string identity)
    {
        var arguments = identity.IndexOf('(');
        var name = arguments < 0 ? identity : identity[..arguments];
        var methodDot = name.LastIndexOf('.');
        if (methodDot < 0) return name;
        var prefix = name[..methodDot];
        return prefix[(prefix.LastIndexOf('.') + 1)..];
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim().TrimStart('.', '/');
    private static string NormalizeGoal(string value) => Guid.TryParse(value, out var id) ? id.ToString("N") : "unknown";
    private static string Short(string value, int length) => value[..Math.Min(value.Length, length)];
    private static string FirstLine(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
}
