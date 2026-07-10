using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record StateEffectProposalApplyResult(
    string Path,
    string Kind,
    string Hash,
    bool Applied,
    string Detail);

internal static class StateEffectProposalApplier
{
    public static IReadOnlyList<StateEffectProposalApplyResult> ApplyLandedProposals(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OrchestratorWorkspace workspace,
        IReadOnlyList<string>? changedFiles,
        Action<string>? writeLine = null)
    {
        var validation = StateEffectProposalParser.ValidateDirectory(workspace.ExecutionDirectory, changedFiles);
        if (!validation.Passed)
        {
            throw new InvalidOperationException("State-effect proposal schema invalid: " + validation.Summary);
        }

        var results = new List<StateEffectProposalApplyResult>();
        foreach (var proposal in validation.Proposals)
        {
            var operation = OperationName(proposal.Hash);
            if (HasCompletedApplication(workspace.ExecutionDirectory, goal.Id, operation))
            {
                var skipped = new StateEffectProposalApplyResult(
                    proposal.RelativePath,
                    proposal.Kind,
                    proposal.Hash,
                    Applied: false,
                    "already applied for this goal/file hash");
                results.Add(skipped);
                writeLine?.Invoke($"State-effect proposal {proposal.RelativePath}: no-op ({skipped.Detail}).");
                continue;
            }

            GoalOperationJournal.Begin(
                workspace.ExecutionDirectory,
                goal,
                operation,
                $"Applying {proposal.Kind} proposal {proposal.RelativePath} ({proposal.Hash}).");
            try
            {
                var detail = ApplyProposal(kernel, goal, workspace, proposal);
                GoalOperationJournal.Completed(workspace.ExecutionDirectory, goal, operation, detail);
                kernel.RecordGoalPolicyDecision(goal.Id, $"State-effect proposal applied: {proposal.RelativePath}; {detail}");
                writeLine?.Invoke($"State-effect proposal {proposal.RelativePath}: {detail}");
                results.Add(new StateEffectProposalApplyResult(
                    proposal.RelativePath,
                    proposal.Kind,
                    proposal.Hash,
                    Applied: true,
                    detail));
            }
            catch (Exception ex)
            {
                GoalOperationJournal.Failed(workspace.ExecutionDirectory, goal, operation, ex.Message);
                throw;
            }
        }

        return results;
    }

    private static string ApplyProposal(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OrchestratorWorkspace workspace,
        StateEffectProposal proposal)
    {
        if (proposal.Kind.Equals(StateEffectProposalKinds.BacklogAdd, StringComparison.Ordinal))
        {
            var title = proposal.Fields["title"].Trim();
            var body = proposal.Body;
            var id = proposal.Fields.TryGetValue("id", out var configuredId) && !string.IsNullOrWhiteSpace(configuredId)
                ? configuredId.Trim()
                : BacklogStore.SlugId(title);
            var sourceGoalId = proposal.Fields.TryGetValue("sourceGoalId", out var source) && !string.IsNullOrWhiteSpace(source)
                ? source.Trim()
                : goal.Id.Value;
            var now = DateTimeOffset.UtcNow;
            var inserted = new BacklogStore(workspace.BacklogStorePath)
                .UpsertAsync(new BacklogItem(id, title, body, BacklogItemStatus.Open, now, now, sourceGoalId))
                .GetAwaiter()
                .GetResult();
            return inserted
                ? $"backlog-add inserted {id}"
                : $"backlog-add no-op existing {id}";
        }

        if (proposal.Kind.Equals(StateEffectProposalKinds.BacklogClose, StringComparison.Ordinal))
        {
            var id = proposal.Fields["id"].Trim();
            var reason = proposal.Fields.TryGetValue("reason", out var configuredReason)
                ? configuredReason.Trim()
                : $"State-effect proposal from goal {goal.Id.Value[..8]} landed.";
            var closed = new BacklogStore(workspace.BacklogStorePath)
                .TryCloseByIdAsync(id, reason)
                .GetAwaiter()
                .GetResult();
            return closed
                ? $"backlog-close closed {id}"
                : $"backlog-close no-op {id}";
        }

        if (proposal.Kind.Equals(StateEffectProposalKinds.GoalRecordNote, StringComparison.Ordinal))
        {
            var note = proposal.Fields.TryGetValue("message", out var message) && !string.IsNullOrWhiteSpace(message)
                ? message.Trim()
                : proposal.Body.Trim();
            kernel.RecordGoalPolicyDecision(goal.Id, note);
            return "goal-record-note recorded";
        }

        throw new InvalidOperationException($"Unknown state-effect proposal kind '{proposal.Kind}'.");
    }

    private static string OperationName(string hash) => $"conductor:state-effect:{hash}";

    private static bool HasCompletedApplication(string executionDirectory, GoalId goalId, string operation) =>
        GoalOperationJournal.Read(executionDirectory, goalId).LatestByOperation.Any(entry =>
            entry.Operation.Equals(operation, StringComparison.OrdinalIgnoreCase) &&
            entry.Status == GoalOperationStatus.Completed);
}
