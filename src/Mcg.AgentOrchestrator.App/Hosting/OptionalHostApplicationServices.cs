using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Hosting;

/// <summary>
/// Narrow transport-neutral boundary used by the optional dashboard host. The web host owns HTTP,
/// request DTOs and rendering; this adapter keeps CLI and orchestration implementation types internal.
/// </summary>
public static class OptionalHostApplicationServices
{
    public static IReadOnlyList<string> NormalizeArguments(string[] args) => CliArgumentParser.NormalizeArgs(args);

    public static bool TryPrintStartupHelp(IReadOnlyList<string> args) => CliCommandHelp.TryPrintStartupHelp(args);

    public static void ThrowIfInvalidFlags(IReadOnlyList<string> args) => CliCommandHelp.ThrowIfInvalidFlags(args);

    public static TaskQuery ParseTaskQuery(IEnumerable<string> tokens) => CliArgumentParser.ParseTaskQuery(tokens);

    public static AgentRole ParseAgentRole(string value) => CliArgumentParser.ParseAgentRole(value);

    public static WorkTaskStatus ParseReportableStatus(string value) => CliArgumentParser.ParseReportableStatus(value);

    public static string BuildSuggestedCommand(
        Goal goal,
        NextActionItem item,
        IReadOnlyList<AgentDefinition>? agents = null) =>
        ConsoleViews.BuildSuggestedCommand(goal, item, agents);

    public static string BuildStageSuggestedCommand(
        Goal goal,
        TaskStageReadiness stage,
        IReadOnlyList<AgentDefinition>? agents = null) =>
        ConsoleViews.BuildStageSuggestedCommand(goal, stage, agents);

    public static string BuildAcceptanceSuggestedCommand(GoalAcceptanceBlocker blocker, int? taskNumber) =>
        ConsoleViews.BuildAcceptanceSuggestedCommand(blocker, taskNumber);

    public static string BuildVerificationSuggestedCommand(int taskNumber, VerificationGateStatus gateStatus) =>
        ConsoleViews.BuildVerificationSuggestedCommand(taskNumber, gateStatus);

    public static int GetTaskDisplayNumber(Goal goal, TaskId taskId) =>
        ConsoleViews.GetTaskDisplayNumber(goal, taskId);

    public static string FormatTaskEvidence(TaskSpec task) => ConsoleViews.FormatTaskEvidence(task);

    public static string? ResolveGoalFriendlyLabel(Goal goal, string backlogStorePath) =>
        CliCommandHandlers.ResolveGoalFriendlyLabel(goal, backlogStorePath);

    public static GoalLifecycleFacts ReadLifecycleFacts(OrchestratorWorkspace workspace, Goal goal) =>
        GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, goal);

    public static IReadOnlyDictionary<string, GoalScopeLifecycleObservation> ReadScopeCollisionLifecycleObservations(
        OrchestratorWorkspace workspace,
        IReadOnlyCollection<Goal> goals) =>
        GoalMonitoringSubscriptionCommand.ReadScopeCollisionLifecycleObservations(workspace, goals);

    public static OperatorInboxReport BuildOperatorInbox(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        OrchestratorWorkspace workspace,
        string? goalPrefix = null,
        bool includeAcknowledged = false) =>
        OperatorInbox.Build(kernel, agents, workerProfiles, workspace, goalPrefix, includeAcknowledged);

    public static OperatorInboxReport AcknowledgeOperatorInbox(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        OrchestratorWorkspace workspace,
        string itemId,
        string? note,
        string? goalPrefix = null) =>
        OperatorInbox.Acknowledge(kernel, agents, workerProfiles, workspace, itemId, note, goalPrefix);

    public static IReadOnlyList<Goal> SelectScopeComparisonCandidates(
        IReadOnlyCollection<Goal> goals,
        string? excludedSourceBacklogItemId) =>
        GoalScopeCollisionAdvisor.SelectComparisonCandidates(goals, excludedSourceBacklogItemId);

    public static GoalScopeCollisionReport BuildScopeCollisionReport(
        IReadOnlyList<string> proposedText,
        IReadOnlyCollection<Goal> goals,
        string? excludedSourceBacklogItemId,
        IReadOnlyDictionary<string, GoalScopeLifecycleObservation>? lifecycleObservations) =>
        GoalScopeCollisionAdvisor.Build(proposedText, goals, excludedSourceBacklogItemId, lifecycleObservations);

    public static Goal CreateAndActivateGoal(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        string objective,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        bool simple) =>
        simple
            ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, objective, workspace, providers)
            : GoalLifecycleCommands.CreateAndActivateGoal(kernel, agents, objective, workspace, providers);

    public static void RecordRefinementPending(AgentOrchestratorKernel kernel, GoalId goalId) =>
        GoalRefinementWorkCoordinator.RecordPending(kernel, goalId);

    public static OrchestratorStateOutboxMessage CreateRefinementMessage(GoalId goalId) =>
        GoalRefinementWorkCoordinator.CreateMessage(goalId);

    public static void LaunchRefinement(OrchestratorWorkspace workspace, GoalId goalId) =>
        _ = GoalRefinementWorkCoordinator.TryLaunch(workspace, goalId);

    public static GoalOperationJournalSummary ReadOperationJournal(string executionDirectory, GoalId goalId) =>
        GoalOperationJournal.Read(executionDirectory, goalId);

    public static bool HasCompletedLandingEvidence(GoalOperationJournalSummary journal) =>
        GoalOperationJournal.HasCompletedLandingEvidence(journal);

    public static bool HasCompletedRecordEvidence(GoalOperationJournalSummary journal) =>
        GoalOperationJournal.HasCompletedRecordEvidence(journal);

    public static bool HasCompletedCleanupEvidence(GoalOperationJournalSummary journal) =>
        GoalOperationJournal.HasCompletedCleanupEvidence(journal);

    public static string[] GetChangedFiles(string workingDirectory) =>
        GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(workingDirectory);
}
