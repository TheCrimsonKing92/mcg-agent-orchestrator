using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class ConductorGitRevisionReader
{
    internal static GateReadyCandidateRevisionPair ReadRequiredPair(string worktreePath) =>
        new(
            ReadRequiredCommit(worktreePath, "HEAD"),
            ReadRequiredCommit(worktreePath, "main^{commit}"));

    internal static string ReadRequiredCommit(string worktreePath, string reference)
    {
        var result = GitCli.Run(worktreePath, 5_000, "rev-parse", "--verify", reference);
        var commit = result.Output.Trim();
        if (!result.Succeeded || !TryNormalize(commit, out var normalized))
        {
            throw new InvalidOperationException(
                $"Landing escalation recheck could not resolve git reference '{reference}'.");
        }

        return normalized;
    }

    internal static bool IsValid(string? revision) => TryNormalize(revision, out _);

    internal static bool TryNormalize(string? revision, out string normalized)
    {
        normalized = revision?.Trim().ToLowerInvariant() ?? string.Empty;
        return CandidateShaPattern().IsMatch(normalized);
    }

    [GeneratedRegex(
        @"^[0-9a-f]{7,64}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CandidateShaPattern();
}
