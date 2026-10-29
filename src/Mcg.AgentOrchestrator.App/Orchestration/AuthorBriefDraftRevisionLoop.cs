using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AuthorBriefDraftRoundRecord(int Round, bool ChecksRun,
    IReadOnlyList<string>? FailedChecks, string? RoundFailure = null, string? RoundFailureDetail = null);

internal sealed record AuthorBriefDraftEvaluation(string Markdown,
    IReadOnlyList<AuthorBriefDraftCheck> Checks, IReadOnlyList<BriefLintFinding> Findings);

internal static class AuthorBriefDraftRevisionLoop
{
    internal const int MaxRevisions = 2;

    internal static AuthorBriefDraftEvaluation Run(string initialPrompt, AuthorBriefDraftEvaluation retained,
        Func<string, WorkerProcessRunResult> dispatch, Func<string, AuthorBriefDraftEvaluation> evaluate,
        Action verifyMainHead, List<AuthorBriefDraftRoundRecord> rounds, CancellationToken cancellationToken)
    {
        for (var revision = 0; revision < MaxRevisions; revision++)
        {
            var failedChecks = retained.Checks.Where(check => !check.Passed).ToArray();
            if (failedChecks.Length == 0) break;
            var prompt = AuthorBriefDraftPrompt.RenderRevision(initialPrompt, retained.Markdown, failedChecks);
            var roundNumber = revision + 2;
            WorkerProcessRunResult round;
            try
            {
                round = dispatch(prompt);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                rounds.Add(new(roundNumber, false, null, "model-failure", $"{ex.GetType().Name}: {ex.Message}"));
                break;
            }
            if (round.ExitCode != 0)
            {
                rounds.Add(new(roundNumber, false, null, "model-failure",
                    $"Author model exited {round.ExitCode}: {round.StandardError}"));
                break;
            }
            var result = AuthorBriefDraftResultParser.Parse(round.StandardOutput);
            if (result is null)
            {
                rounds.Add(new(roundNumber, false, null, "unparseable",
                    "Author did not return one valid draft or stale JSON object."));
                break;
            }
            // Repository changes retain the service's existing failure path, not revision recovery.
            verifyMainHead();
            if (result.Kind == "stale")
            {
                rounds.Add(new(roundNumber, false, null, "stale", result.Reason));
                break;
            }
            retained = evaluate(result.Markdown!);
            rounds.Add(new(roundNumber, true, retained.Checks.Where(check => !check.Passed)
                .Select(check => check.Name).ToArray()));
        }
        return retained;
    }
}
