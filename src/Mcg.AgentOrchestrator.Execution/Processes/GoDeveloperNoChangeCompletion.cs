using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GoDeveloperNoChangeCompletion
{
    private static readonly Regex TestIdentity = new(
        @"^[a-z_][a-z0-9_]*[.:]Test[A-Z0-9_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> ParseTestIdentities(string testsField)
    {
        var deferred = testsField.IndexOf("deferred", StringComparison.OrdinalIgnoreCase);
        if (deferred < 0) return [];
        var declaration = testsField[(deferred + "deferred".Length)..];
        declaration = declaration.TrimStart(' ', '\t', ':', '-', '\u2013', '\u2014');
        declaration = declaration.Split([';', '\r', '\n'], 2)[0];
        return Regex.Split(declaration, @"\s*,\s*|\s+and\s+", RegexOptions.CultureInvariant)
            .Select(entry => entry.Trim().Trim('`'))
            .TakeWhile(entry => TestIdentity.IsMatch(entry)).ToArray();
    }

    internal static bool TryAccept(TaskSpec task, string worktreePath, string head, bool clean,
        bool hasRelevantCommitAfterDispatch, string standardOutput, out string marker)
    {
        marker = string.Empty;
        if (task.RequiredRole != AgentRole.Developer || !clean || hasRelevantCommitAfterDispatch ||
            task.LastDispatch is not { } current || string.IsNullOrWhiteSpace(current.BaseCommit) ||
            !string.Equals(current.BaseCommit, head, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(worktreePath) ||
            TargetToolchainDetector.Detect(worktreePath) != Toolchain.Go)
            return false;

        var prior = task.DispatchHistory
            .Where(dispatch => dispatch.DispatchedAt < current.DispatchedAt &&
                !string.IsNullOrWhiteSpace(dispatch.ResultCommit) &&
                !string.Equals(dispatch.ResultCommit, dispatch.BaseCommit, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.DispatchedAt).FirstOrDefault();
        if (prior is null || !WorkerResultParser.TryParseResult(standardOutput, out var result, out _) ||
            !IsField("files", "none") || !IsField("commit", "none") ||
            !IsField("assigned_scope_complete", "true") ||
            result.BlockersStatus != WorkerResultParser.BlockersStatus.None ||
            result.TestsStatus != WorkerResultParser.TestsStatus.Deferred)
            return false;

        var identities = ParseTestIdentities(result.Fields["tests"]);
        if (identities.Count == 0) return false;
        marker = $"GO_DEVELOPER_NO_CHANGE_ACCEPTED prior_commit={prior.ResultCommit} " +
            $"candidate_sha={current.BaseCommit} tests={string.Join(",", identities)}";
        return true;

        bool IsField(string key, string expected) => result.Fields.TryGetValue(key, out var value) &&
            string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }
}
