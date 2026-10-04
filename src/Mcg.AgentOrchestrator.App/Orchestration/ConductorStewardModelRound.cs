using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorStewardModelRound
{
    string? ModelAlias => null;
    Task<string> DispatchAsync(ConductorStewardTrigger trigger, string workingDirectory, CancellationToken cancellationToken);
}

internal sealed class ClaudeConductorStewardModelRound(
    string receiptDirectory,
    Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcessAsync = null,
    IConductorStewardTrackedFileLister? files = null,
    Func<string, string, bool>? pathExists = null,
    Func<Guid>? newSessionId = null,
    ConductorLessonSelector? lessons = null,
    ModelFunctionCatalog? catalog = null) : IConductorStewardModelRound
{
    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> _runProcessAsync =
        runProcessAsync ?? WorkerProcessRunner.RunBufferedAsync;
    private readonly IConductorStewardTrackedFileLister _files = files ?? new GitConductorStewardTrackedFileLister();
    private readonly Func<string, string, bool> _pathExists = pathExists ?? ConductorStewardEvidenceBundle.PathExists;
    private readonly Func<Guid> _newSessionId = newSessionId ?? Guid.NewGuid;

    public string? ModelAlias => ConductorRoundModelResolver.Resolve(catalog, ModelFunctionPurposes.ConductorSteward).Alias;

    public async Task<string> DispatchAsync(
        ConductorStewardTrigger trigger, string workingDirectory, CancellationToken cancellationToken)
    {
        var model = ConductorRoundModelResolver.Resolve(catalog, ModelFunctionPurposes.ConductorSteward);
        var sessionId = _newSessionId().ToString("D");
        if (!model.IsValid)
        {
            WriteReceipt(trigger, sessionId, null, null, null, model.InvalidReason, null);
            throw new ConductorModelRoundException(model.InvalidReason!, null);
        }
        var selected = lessons?.Select(ConductorLessonSelector.StewardTags(trigger.Kind));
        var bundle = ConductorStewardEvidenceBundle.Build(trigger, workingDirectory, _files, _pathExists, selected);
        var prompt = $"""
            You are the conductor Steward. This is a read-only adjudication. Return exactly one fenced JSON object.
            Allowed kinds: {(trigger.Kind is ConductorStewardTriggerKind.DeveloperGateReopenNoCommit or ConductorStewardTriggerKind.DeveloperReviewerFindingNoCommit ? "close, ask-owner, no-action" : "route, ask-owner, no-action")}. A route has fields kind, targetTaskId,
            cause (NewTestFinding or ContractClarification), text (diagnosis), instruction,
            evidenceReferences (array), reversibility (reversible or reversible-with-cost), and precedent.
            An ask-owner has kind, question, and evidenceReferences. A no-action has kind and reason.
            You may not choose another task or an irreversible action. When evidence is insufficient, ask-owner.
            {(trigger.Kind == ConductorStewardTriggerKind.DeveloperGateReopenNoCommit
                ? "For case D return close, ask-owner, or no-action. A close has targetTaskId, text (diagnosis), and evidenceReferences; the host appends the Developer WORKER_RESULT verbatim. Inspect failing tests, changed paths, genuine reason and passing candidate receipts before closing; insufficient evidence requires ask-owner."
                : trigger.Kind == ConductorStewardTriggerKind.DeveloperReviewerFindingNoCommit
                ? "For case E judge only whether the Developer's no-change explanation answers the Reviewer finding in the trigger evidence. If sufficient return close with targetTaskId, text (diagnosis), and evidenceReferences; the host closes the Developer with its own WORKER_RESULT and retries the Reviewer with that result verbatim. The Reviewer remains the judge of whether the finding is resolved; you do not resolve or waive it. If insufficient return ask-owner naming the Developer task and unanswered finding."
                : string.Empty)}

            Case: {trigger.CaseLetter}; trigger: {trigger.Identity}; task: {trigger.TaskId}
            WORKER_RESULT:
            {trigger.WorkerResult}
            Trigger evidence:
            {trigger.Evidence}
            Evidence references: {string.Join(", ", trigger.EvidenceReferences)}
            {bundle}
            Current refined acceptance criteria:
            {string.Join(Environment.NewLine, trigger.AcceptanceCriteria.Select((criterion, index) => $"{index + 1}. {criterion}"))}
            """;
        WorkerProcessRunResult result;
        try
        {
            result = await _runProcessAsync(
                new WorkerProcessRunRequest($"claude {model.ModelArguments} --permission-mode plan --tools 'Read,Grep,Glob' --allowed-tools 'Read,Grep,Glob' --session-id {sessionId} -p",
                    workingDirectory, TimeSpan.FromMinutes(4), prompt), cancellationToken);
            WriteReceipt(trigger, sessionId, result.ExitCode, result.StandardOutput, result.StandardError, null, model.Alias);
        }
        catch (Exception ex)
        {
            WriteReceipt(trigger, sessionId, null, null, null, $"{ex.GetType().Name}: {ex.Message}", model.Alias);
            throw;
        }
        if (result.ExitCode != 0)
            throw new ConductorModelRoundException($"Steward model exited {result.ExitCode}: {result.StandardError}", model.Alias);
        return result.StandardOutput;
    }

    private void WriteReceipt(ConductorStewardTrigger trigger, string sessionId, int? exitCode,
        string? stdout, string? stderr, string? failure, string? model)
    {
        Directory.CreateDirectory(receiptDirectory);
        var path = Path.Combine(receiptDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            trigger = trigger.Identity,
            sessionId,
            completedAt = DateTimeOffset.UtcNow,
            exitCode,
            stdout,
            stderr,
            failure,
            model
        }));
    }
}
