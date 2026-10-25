using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Authorizes protected content for one goal; ordinary paths never enter its fingerprint.
internal static class LandingDenylistOwnerGate
{
    internal sealed record Result(
        IReadOnlyList<LandingDenylist.MatchResult> Matches, string Fingerprint, bool Approved, string HoldReason);

    internal static string ContentFingerprint(string executionDirectory, string revision, IEnumerable<string> paths)
    {
        if (!RevisionExists(executionDirectory, revision))
            throw new InvalidOperationException($"Cannot resolve approval revision {revision}.");
        var lines = new StringBuilder();
        foreach (var path in paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var blob = Git(executionDirectory, "rev-parse", "--verify", "--quiet", $"{revision}:{path}");
            var objectId = blob.Output.Trim();
            if (blob.ExitCode != 0)
            {
                // A failed command is not evidence of absence. Confirm against the resolved tree.
                var listing = Git(executionDirectory, "ls-tree", "--name-only", revision, "--", path);
                if (blob.ExitCode != 1 || listing.ExitCode != 0 || !string.IsNullOrWhiteSpace(listing.Output))
                    throw new InvalidOperationException($"Cannot read protected path {path} at {revision}: {blob.Error} {listing.Error}");
                objectId = "absent";
            }
            else if (!AcceptancePolicyChangeDecision.IsFullSha(objectId))
                throw new InvalidOperationException($"Invalid object id for protected path {path} at {revision}.");
            lines.Append(path).Append('\t').Append(objectId).Append('\n');
        }
        return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString()))).ToLowerInvariant();
    }

    internal static Result Evaluate(
        string executionDirectory, LandingDenylist denylist, ICollaborationItemStore decisions,
        string goalId, string mainRevision, string candidateHeadRevision, IEnumerable<string> changedPaths)
    {
        var matches = denylist.Match(changedPaths);
        if (matches.Count == 0) return new(matches, string.Empty, true, string.Empty);
        var fingerprint = "unavailable";
        string? diagnostic = null;
        try
        {
            var paths = matches.Select(match => match.Path).Distinct(StringComparer.Ordinal).ToArray();
            fingerprint = ContentFingerprint(executionDirectory, candidateHeadRevision, paths);
            if (AcceptancePolicyChangeDecision.IsApprovedAsync(decisions, goalId, candidateHeadRevision).GetAwaiter().GetResult())
                return new(matches, fingerprint, true, string.Empty);
            var prefix = AcceptancePolicyChangeDecision.RequestId(goalId, string.Empty);
            foreach (var request in decisions.ListDecisionRequestsAsync(goalId).GetAwaiter().GetResult())
            {
                if (!request.Id.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var sha = request.Id[prefix.Length..];
                if (!AcceptancePolicyChangeDecision.IsApprovedAsync(decisions, goalId, sha).GetAwaiter().GetResult() ||
                    !RevisionExists(executionDirectory, sha)) continue;
                if (ContentFingerprint(executionDirectory, sha, paths) == fingerprint)
                    return new(matches, fingerprint, true, string.Empty);
            }
        }
        catch (Exception exception)
        {
            diagnostic = $"main={mainRevision} {exception.Message.Replace('\r', ' ').Replace('\n', ' ')}";
        }
        var goalPrefix = goalId[..8];
        var reason = $"landing-denylist owner approval required: goal={goalPrefix} candidate={candidateHeadRevision}" +
            string.Concat(matches.Select(match => $" rule={match.RuleId} path={match.Path}")) +
            $" fingerprint={fingerprint} approve: approve-policy-change {goalPrefix} {candidateHeadRevision} --text-file <reason-file>";
        if (diagnostic is not null) reason += $" diagnostic={diagnostic}";
        return new(matches, fingerprint, false, reason);
    }

    private static bool RevisionExists(string directory, string revision)
    {
        var result = Git(directory, "rev-parse", "--verify", "--quiet", $"{revision}^{{commit}}");
        if (result.ExitCode == 1) return false;
        if (result.ExitCode != 0 || !AcceptancePolicyChangeDecision.IsFullSha(result.Output.Trim()))
            throw new InvalidOperationException($"Cannot resolve revision {revision}: {result.Error}");
        return true;
    }

    private static GitCli.GitResult Git(string directory, params string[] arguments)
    {
        var result = LandingExecutor.GitRunner(directory, arguments);
        if (!result.ProcessStarted) throw new InvalidOperationException($"Git did not start: {result.Error}");
        if (result.DrainTimedOut) throw new InvalidOperationException($"Git output drain timed out: {string.Join(' ', arguments)}");
        return result;
    }
}
