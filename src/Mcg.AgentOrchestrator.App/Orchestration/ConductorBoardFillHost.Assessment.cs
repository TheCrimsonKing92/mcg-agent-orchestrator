using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BoardFillRoundResult(AuthorBriefDraftOutcome Outcome, string? Markdown,
    BoardFillPremiseVerification Verification);

internal sealed partial class ConductorBoardFillHost
{
    private async Task<BoardFillRoundResult> RunRoundAsync(string id)
    {
        // The draft seam's exceptions remain faulted tasks for the existing harvest contract.
        var outcome = _draft(id, _shutdown.Token);
        string? markdown = null;
        if (outcome.Kind == "draft" && outcome.DraftPath is { } path)
        {
            try { markdown = await File.ReadAllTextAsync(path, _shutdown.Token).ConfigureAwait(false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (OperationCanceledException) { }
        }
        var premise = BoardFillVerifierContract.Premise(markdown ?? "");
        var verification = new BoardFillPremiseVerification("unavailable", BoardFillVerifierContract.BulletCount(premise), [],
            markdown is null ? "draft-unreadable" : outcome.MainHead is null ? "missing-main-head" : "verifier-not-configured");
        if (markdown is not null && outcome.MainHead is { } head && _verifier is not null)
        {
            try { verification = await _verifier.VerifyAsync(premise, head, _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { verification = verification with { Status = "failed", Detail = "cancelled" }; }
            catch (Exception exception) { verification = verification with { Status = "failed", Detail = exception.GetType().Name }; }
        }
        return new(outcome, markdown, verification);
    }

    private void Assess(BoardFillDraftRound round, string? markdown, BoardFillPremiseVerification verification,
        AgentOrchestratorKernel kernel)
    {
        var items = _backlog();
        var goals = kernel.Goals.ToArray();
        _store.Assess(round.Id, BoardFillFileability.Evaluate(round, markdown, verification,
            BoardFillPreflightChecks.Run(markdown ?? ""), BoardFillScopeDepends.Propose(markdown ?? "", goals, round.BacklogItemId),
            items.FirstOrDefault(item => item.Id == round.BacklogItemId), goals, _readiness(kernel, items)));
    }
}
