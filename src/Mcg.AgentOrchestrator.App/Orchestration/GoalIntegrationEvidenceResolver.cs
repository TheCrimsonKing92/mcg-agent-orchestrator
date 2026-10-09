using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalIntegrationEvidence(
    string IntegrateSha,
    string MainSha,
    string Subject);

internal interface IGoalIntegrationEvidenceResolver
{
    bool TryResolve(GoalId goalId, out GoalIntegrationEvidence? evidence);
}

internal sealed class GoalIntegrationEvidenceResolver : IGoalIntegrationEvidenceResolver
{
    private const string SubjectPrefix = "Integrate goal/";
    private readonly string _executionDirectory;
    private readonly string? _mainSha;
    private readonly IReadOnlyList<IntegrationCommitCandidate> _candidates;
    private readonly Func<string, IReadOnlyList<string>, GitCli.GitResult> _gitRunner;
    private readonly Dictionary<GoalId, GoalIntegrationEvidence?> _cache = [];

    internal static Func<string, IReadOnlyList<string>, GitCli.GitResult> GitRunner { get; set; } =
        (workingDirectory, args) => GitCli.Run(workingDirectory, args.ToArray());

    private GoalIntegrationEvidenceResolver(
        string executionDirectory,
        string? mainSha,
        IReadOnlyList<IntegrationCommitCandidate> candidates,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        _executionDirectory = Path.GetFullPath(executionDirectory);
        _mainSha = mainSha;
        _candidates = candidates;
        _gitRunner = gitRunner;
    }

    public static GoalIntegrationEvidenceResolver Build(
        string executionDirectory, string integrationBranch,
        string? knownMainSha = null,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null)
    {
        gitRunner ??= GitRunner;
        var fullExecutionDirectory = Path.GetFullPath(executionDirectory);
        var mainSha = string.IsNullOrWhiteSpace(knownMainSha)
            ? ResolveMainSha(fullExecutionDirectory, integrationBranch, gitRunner)
            : knownMainSha.Trim();
        if (string.IsNullOrWhiteSpace(mainSha))
        {
            return new GoalIntegrationEvidenceResolver(fullExecutionDirectory, null, [], gitRunner);
        }

        var log = RunGit(
            gitRunner,
            fullExecutionDirectory,
            "log",
            mainSha,
            "--format=%H%x09%s",
            $"--grep=^{SubjectPrefix}");
        var candidates = log.ExitCode == 0
            ? ParseCandidates(log.Output)
            : [];
        return new GoalIntegrationEvidenceResolver(fullExecutionDirectory, mainSha, candidates, gitRunner);
    }

    public bool TryResolve(GoalId goalId, out GoalIntegrationEvidence? evidence)
    {
        if (_cache.TryGetValue(goalId, out evidence))
        {
            return evidence is not null;
        }

        evidence = null;
        if (string.IsNullOrWhiteSpace(_mainSha))
        {
            _cache[goalId] = null;
            return false;
        }

        var candidate = _candidates.FirstOrDefault(item => MatchesGoal(item.GoalToken, goalId.Value));
        if (candidate is null)
        {
            _cache[goalId] = null;
            return false;
        }

        // The integrate commit being an ancestor of current main is the lifecycle fact. A later
        // revert deliberately does not reopen the old goal: the revert is separate work with its
        // own lifecycle, and attempting content-aware revert detection would make goals flap.
        var ancestry = RunGit(
            _gitRunner,
            _executionDirectory,
            "merge-base",
            "--is-ancestor",
            candidate.Sha,
            _mainSha);
        if (ancestry.ExitCode == 0)
        {
            evidence = new GoalIntegrationEvidence(candidate.Sha, _mainSha, candidate.Subject);
        }

        _cache[goalId] = evidence;
        return evidence is not null;
    }

    internal static IReadOnlyList<IntegrationCommitCandidate> ParseCandidates(string output)
    {
        var candidates = new List<IntegrationCommitCandidate>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('\t');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            var sha = line[..separator].Trim();
            var subject = line[(separator + 1)..].Trim();
            if (!subject.StartsWith(SubjectPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var token = subject[SubjectPrefix.Length..]
                .Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(sha) && !string.IsNullOrWhiteSpace(token))
            {
                candidates.Add(new IntegrationCommitCandidate(sha, subject, token));
            }
        }

        return candidates;
    }

    private static string? ResolveMainSha(
        string executionDirectory, string integrationBranch,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        var result = RunGit(gitRunner, executionDirectory, "rev-parse", "--verify", $"refs/heads/{integrationBranch}");
        var sha = result.Output.Trim();
        return result.ExitCode == 0 && sha.Length > 0 && !sha.Any(char.IsWhiteSpace)
            ? sha
            : null;
    }

    private static bool MatchesGoal(string token, string goalId) =>
        token.Length >= 8 &&
        (goalId.StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
         token.StartsWith(goalId, StringComparison.OrdinalIgnoreCase));

    private static GitCli.GitResult RunGit(
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner,
        string executionDirectory,
        params string[] args) =>
        gitRunner(executionDirectory, args);

    internal sealed record IntegrationCommitCandidate(string Sha, string Subject, string GoalToken);
}
