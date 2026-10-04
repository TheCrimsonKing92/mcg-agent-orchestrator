using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns a seeded repository; no processes are dispatched.
public sealed class WorkerDispatchShadowCascadeTests : WorkerDispatchTestSupport
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-24T01:00:00Z");

    [Fact(DisplayName = "Shadow recording preserves every prepared execution input")]
    public void RecorderPresenceChangesOnlyShadowEvidence()
    {
        var root = CreateSeededDispatchRepository();
        try
        {
            var absent = Prepare(root, null);
            var active = Prepare(root, DispatchShadowRecorder.Default);
            AssertExecutionEqual(absent, active);
            Assert.Null(absent.Dispatch.ShadowDecision);
            Assert.Equal(new DispatchShadowDecision(DispatchTaskClass.DocsOnly, "OpenAI", "model", "low",
                ShadowCascadePolicy.PolicyVersion, true), active.Dispatch.ShadowDecision);
            var restored = AgentOrchestratorKernel.FromSnapshot(active.Kernel.ExportSnapshot());
            Assert.Equal(active.Dispatch.ShadowDecision, restored.Goals.Single().Tasks.Single().LastDispatch!.ShadowDecision);
        }
        finally { ScriptSandboxCleanup.DeleteDirectoryTree(root); }
    }

    [Fact(DisplayName = "Classifier failure records unrecorded and preserves dispatch execution")]
    public void ThrowingRecorderCannotFailPreparation()
    {
        var root = CreateSeededDispatchRepository();
        try
        {
            var absent = Prepare(root, null);
            var calls = 0;
            var failed = Prepare(root, new DispatchShadowRecorder((_, _) =>
            {
                calls++;
                throw new InvalidOperationException("classification failure");
            }));
            Assert.Equal(1, calls);
            AssertExecutionEqual(absent, failed);
            Assert.Equal(new DispatchShadowDecision(DispatchTaskClass.Unrecorded, "OpenAI", "model", "medium",
                ShadowCascadePolicy.PolicyVersion, false), failed.Dispatch.ShadowDecision);
            var restored = AgentOrchestratorKernel.FromSnapshot(failed.Kernel.ExportSnapshot());
            Assert.Equal(failed.Dispatch.ShadowDecision, restored.Goals.Single().Tasks.Single().LastDispatch!.ShadowDecision);
        }
        finally { ScriptSandboxCleanup.DeleteDirectoryTree(root); }
    }

    private static Prepared Prepare(string root, DispatchShadowRecorder? recorder)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var task = new TaskSpec(new TaskId("shadow-task"), "Update `docs/guide.md`.", AgentRole.Developer);
        var goal = kernel.CreateGoal(new GoalId("shadow-goal"), "Maintain `docs/guide.md`.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var result = WorkerProfileDispatcher.PrepareTask(kernel, goal, task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(root, "prompts"), root, At, providerName: "OpenAI", modelName: "model",
            reasoningEffort: "medium", shadowRecorder: recorder);
        return new(kernel, result.Task.LastDispatch!, File.ReadAllBytes(result.PromptPath));
    }

    private static void AssertExecutionEqual(Prepared expected, Prepared actual)
    {
        // Command includes all substituted arguments; these are the persisted process environment inputs.
        Assert.Equal(expected.Dispatch.Command, actual.Dispatch.Command);
        Assert.Equal(expected.Dispatch.WorkingDirectory, actual.Dispatch.WorkingDirectory);
        Assert.Equal(expected.Dispatch.WorkerName, actual.Dispatch.WorkerName);
        Assert.Equal(expected.Dispatch.DispatchLane, actual.Dispatch.DispatchLane);
        Assert.Equal(expected.Dispatch.ClaudeCredentialSourceDirectory, actual.Dispatch.ClaudeCredentialSourceDirectory);
        Assert.Equal(expected.Dispatch.ClaudeCredentialSourceIsExplicit, actual.Dispatch.ClaudeCredentialSourceIsExplicit);
        Assert.Equal(expected.Dispatch.ProviderSessionId, actual.Dispatch.ProviderSessionId);
        Assert.Equal(expected.Dispatch.WorktreeHeadSha, actual.Dispatch.WorktreeHeadSha);
        Assert.Equal(expected.Dispatch.DirtyStateHash, actual.Dispatch.DirtyStateHash);
        Assert.Equal(expected.Dispatch.SandboxLowIntegrity, actual.Dispatch.SandboxLowIntegrity);
        Assert.Equal(expected.Dispatch.ProviderName, actual.Dispatch.ProviderName);
        Assert.Equal(expected.Dispatch.ModelName, actual.Dispatch.ModelName);
        Assert.Equal(expected.Dispatch.ReasoningEffort, actual.Dispatch.ReasoningEffort);
        Assert.Equal(expected.Prompt, actual.Prompt);
    }

    private sealed record Prepared(AgentOrchestratorKernel Kernel, TaskDispatchRecord Dispatch, byte[] Prompt);
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => At; }
}
