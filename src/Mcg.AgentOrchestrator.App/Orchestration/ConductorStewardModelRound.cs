using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorStewardModelRound
{
    Task<string> DispatchAsync(ConductorStewardTrigger trigger, string workingDirectory, CancellationToken cancellationToken);
}

internal sealed class ClaudeConductorStewardModelRound(
    string receiptDirectory,
    Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcessAsync = null,
    IConductorStewardTrackedFileLister? files = null,
    Func<string, string, bool>? pathExists = null,
    Func<Guid>? newSessionId = null,
    ConductorLessonSelector? lessons = null) : IConductorStewardModelRound
{
    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> _runProcessAsync =
        runProcessAsync ?? WorkerProcessRunner.RunBufferedAsync;
    private readonly IConductorStewardTrackedFileLister _files = files ?? new GitConductorStewardTrackedFileLister();
    private readonly Func<string, string, bool> _pathExists = pathExists ?? ConductorStewardEvidenceBundle.PathExists;
    private readonly Func<Guid> _newSessionId = newSessionId ?? Guid.NewGuid;

    public async Task<string> DispatchAsync(
        ConductorStewardTrigger trigger, string workingDirectory, CancellationToken cancellationToken)
    {
        var selected = lessons?.Select(ConductorLessonSelector.StewardTags(trigger.Kind));
        var bundle = ConductorStewardEvidenceBundle.Build(trigger, workingDirectory, _files, _pathExists, selected);
        var sessionId = _newSessionId().ToString("D");
        var prompt = $"""
            You are the conductor Steward. This is a read-only adjudication. Return exactly one fenced JSON object.
            Allowed kinds: route, ask-owner, no-action. A route has fields kind, targetTaskId,
            cause (NewTestFinding or ContractClarification), text (diagnosis), instruction,
            evidenceReferences (array), reversibility (reversible or reversible-with-cost), and precedent.
            An ask-owner has kind, question, and evidenceReferences. A no-action has kind and reason.
            You may not choose another task or an irreversible action. When evidence is insufficient, ask-owner.

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
                new WorkerProcessRunRequest($"claude --model sonnet --permission-mode plan --tools 'Read,Grep,Glob' --allowed-tools 'Read,Grep,Glob' --session-id {sessionId} -p",
                    workingDirectory, TimeSpan.FromMinutes(4), prompt), cancellationToken);
            WriteReceipt(trigger, sessionId, result.ExitCode, result.StandardOutput, result.StandardError, null);
        }
        catch (Exception ex)
        {
            WriteReceipt(trigger, sessionId, null, null, null, $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Steward model exited {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }

    private void WriteReceipt(ConductorStewardTrigger trigger, string sessionId, int? exitCode,
        string? stdout, string? stderr, string? failure)
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
            failure
        }));
    }
}
