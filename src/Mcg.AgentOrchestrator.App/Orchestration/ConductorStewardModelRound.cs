using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorStewardModelRound
{
    Task<string> DispatchAsync(ConductorStewardTrigger trigger, string workingDirectory, CancellationToken cancellationToken);
}

internal sealed class ClaudeConductorStewardModelRound : IConductorStewardModelRound
{
    public async Task<string> DispatchAsync(
        ConductorStewardTrigger trigger, string workingDirectory, CancellationToken cancellationToken)
    {
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
            Current refined acceptance criteria:
            {string.Join(Environment.NewLine, trigger.AcceptanceCriteria.Select((criterion, index) => $"{index + 1}. {criterion}"))}
            """;
        var result = await WorkerProcessRunner.RunBufferedAsync(
            new WorkerProcessRunRequest("claude --model sonnet --permission-mode plan -p",
                workingDirectory, TimeSpan.FromMinutes(10), prompt), cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Steward model exited {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }
}
