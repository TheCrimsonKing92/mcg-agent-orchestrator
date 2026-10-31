using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Fixtures own their roots; this collection protects command console state.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class StoppedGoalTaskClosureTests : CliCommandTestBase
{
    [Theory]
    [InlineData("abandon-goal", null)]
    [InlineData("stop", "abandon")]
    [InlineData("cancel-goal", null)]
    [InlineData("stop", "cancel")]
    [InlineData("supersede-goal", null)]
    [InlineData("stop", "supersede")]
    public void EveryCliStopRouteClosesOpenTasksAfterTheStopEvent(string verb, string? mode)
    {
        var root = CreateTempDirectory();
        try
        {
            EnsureGitRepository(root);
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var assigned = new TaskSpec(TaskId.New(), "Assigned work", AgentRole.Developer);
            var waiting = new TaskSpec(TaskId.New(), "Await operator", AgentRole.Reviewer);
            var goal = kernel.CreateGoal("Stop open work", [assigned, waiting]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var request = kernel.RequestHumanInput(goal.Id, waiting.Id, "Continue?", HumanWaitKind.SpecClarification);
            // Pin the Active fixture while retaining the real request and WaitingForHuman task.
            kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with { Status = GoalStatus.Active });
            goal = kernel.GetGoal(goal.Id);
            Assert.Equal(GoalStatus.Active, goal.Status);
            Assert.Equal(WorkTaskStatus.Assigned, assigned.Status);
            Assert.Equal(WorkTaskStatus.WaitingForHuman, waiting.Status);
            Assert.False(request.IsCompleted);
            const string reason = "Operator stopped this work.";
            var confirmation = verb == "abandon-goal" || mode == "abandon"
                ? "--confirm-goal-abandon" : "--confirm-goal-stop";
            string[] parts = mode is null
                ? [verb, goal.Id.Value[..8], reason, confirmation]
                : [verb, goal.Id.Value[..8], reason, "--as", mode, confirmation];

            ExecuteStop(parts, kernel, workspace);

            Assert.Equal(verb == "supersede-goal" || mode == "supersede"
                ? GoalStatus.Superseded : GoalStatus.Cancelled, goal.Status);
            Assert.All(goal.Tasks, task => Assert.Equal(WorkTaskStatus.Cancelled, task.Status));
            Assert.True(kernel.HumanInputRequests.Single(item => item.Id == request.Id).IsCompleted);
            var stopEvent = Assert.Single(goal.Timeline.Where(item =>
                item.Kind is ProgressKind.GoalCancelled or ProgressKind.GoalSuperseded));
            Assert.Equal(reason, stopEvent.Message);
            var indexedEvents = goal.Timeline.Select((item, index) => (Event: item, Index: index)).ToArray();
            var stopIndex = indexedEvents.Single(item => item.Event == stopEvent).Index;
            var cancellations = indexedEvents.Where(item => item.Event.Kind == ProgressKind.TaskCancelled).ToArray();
            Assert.Equal(2, cancellations.Length);
            Assert.All(cancellations, item => Assert.True(stopIndex < item.Index));
            Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.StartsWith("goal stop: goal is ", StringComparison.Ordinal)));
            var sweep = TerminalGoalSweep.Run(kernel, root, TrunkBranchName.Default, goal.Id);
            Assert.Empty(sweep.Goals);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void AbandonClosesTaskWhoseTrackedProcessExited()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var assigned = new TaskSpec(TaskId.New(), "Assigned work", AgentRole.Developer);
            var exited = new TaskSpec(TaskId.New(), "Exited work", AgentRole.Tester);
            var goal = kernel.CreateGoal("Abandon exited dispatch", [assigned, exited]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            RecordRunningProcess(kernel, goal, exited, root, processId: 999999);
            Assert.Equal(GoalStatus.Active, goal.Status);
            Assert.Equal(WorkTaskStatus.Assigned, assigned.Status);
            Assert.Equal(WorkTaskStatus.Running, exited.Status);

            ExecuteStop(["abandon-goal", goal.Id.Value[..8], "Stop exited work.", "--confirm-goal-abandon"],
                kernel, workspace);

            Assert.Equal(GoalStatus.Cancelled, goal.Status);
            Assert.All(goal.Tasks, task => Assert.Equal(WorkTaskStatus.Cancelled, task.Status));
            Assert.NotNull(exited.LastProcess);
            Assert.Equal(999999, exited.LastProcess.ProcessId);
            Assert.Null(exited.LastProcess.CompletedAt);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void AbandonKeepsTaskWithLiveTrackedProcess()
    {
        var root = CreateTempDirectory();
        try
        {
            EnsureGitRepository(root);
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var assigned = new TaskSpec(TaskId.New(), "Assigned work", AgentRole.Developer);
            var live = new TaskSpec(TaskId.New(), "Live work", AgentRole.Tester);
            var goal = kernel.CreateGoal("Abandon with live dispatch", [assigned, live]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            RecordRunningProcess(kernel, goal, live, root, Environment.ProcessId);
            var liveStatus = live.Status;
            Assert.Equal(GoalStatus.Active, goal.Status);
            Assert.Equal(WorkTaskStatus.Assigned, assigned.Status);
            Assert.Equal(WorkTaskStatus.Running, liveStatus);

            // abandon-goal leaves running processes alone; the stop alias would terminate them.
            ExecuteStop(["abandon-goal", goal.Id.Value[..8], "Leave live work running.", "--confirm-goal-abandon"],
                kernel, workspace);

            Assert.Equal(GoalStatus.Cancelled, goal.Status);
            Assert.Equal(WorkTaskStatus.Cancelled, assigned.Status);
            Assert.Equal(liveStatus, live.Status);
            var decision = Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.StartsWith("goal stop: goal is ", StringComparison.Ordinal)));
            Assert.Equal("goal stop: goal is Cancelled; cancelled 1 open task(s) with no live tracked process.", decision.Message);
            var sweep = TerminalGoalSweep.Run(kernel, root, TrunkBranchName.Default, goal.Id);
            Assert.DoesNotContain(sweep.Goals.Where(item => item.GoalId == goal.Id).SelectMany(item => item.Repairs),
                repair => repair.Kind == "terminal-task-desync");
            Assert.Equal(liveStatus, live.Status);
            Assert.Single(goal.Tasks.Where(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private void ExecuteStop(string[] parts, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        CaptureConsole(() => Assert.True(CliCommandDispatcher.ExecuteCommand(parts, kernel, workspace,
            ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current,
            cleanupContext: CreateIsolatedCleanupContext(workspace))));
    }
}
