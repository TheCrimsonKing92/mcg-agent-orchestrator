using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalLifecycleOutboxStdoutParityTests : CliGoalLifecycleOutboxTestSupport
{
    // Fixed-seed outputs transcribed from main 18438ee33's unchanged renderers and plan.
    // The missing-lease seed prints no paths, ids or timestamps beyond the pinned goal id.
    private const string MainUnparkOutput = "Goal unparked aaaaaaaa.\nStatus change: Parked -> Active\n";
    private const string MainSupersedeOutput =
        "\nGoal aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nObjective: Lifecycle target\nStatus: Superseded\nTasks:\n" +
        "  1. [Pending] Developer: Lifecycle task (unassigned)\n\n";
    private const string MainAbandonOutput =
        "Goal abandon aaaaaaaa Cancelled: Operator transition\n" +
        "Dry run: False\nCan apply: True\nSteps:\n" +
        "  GoalStatus: Keep; Goal is already Cancelled.\n" +
        "  RunningDispatches: Keep; No running dispatch process records.\n" +
        "  Worktree: Missing; No goal worktree is registered.\n" +
        "  BuildLease: Missing; goal build lease is missing\n" +
        "  Retention: Keep; Preserve logs, journals, transcripts, and context packages as audit evidence; inspect retention-plan for archive decisions.\n" +
        "    command: retention-plan aaaaaaaa\n" +
        "Retention:\n" +
        "  Worktree: Archive; exists=False\n" +
        "  ContextPackage: Archive; exists=False\n" +
        "  WorkerLogs: Keep; exists=False\n" +
        "  BuildLease: DeleteWhenSafe; exists=False\n" +
        "  TestEvidence: Archive; exists=False\n" +
        "  TestEvidence: Archive; exists=False\n" +
        "  OperationJournal: Keep; exists=True\n" +
        "  Transcript: Archive; exists=False\n";

    [Xunit.Theory]
    [Xunit.InlineData("unpark")]
    [Xunit.InlineData("supersede")]
    [Xunit.InlineData("abandon")]
    [Xunit.InlineData("stop-abandon")]
    public async Task SuccessfulTransition_MatchesMainStdoutAndMemory_RetiresDelivery(string command)
    {
        using var seed = await Seed.Create(command);
        // Fail before invoking cleanup if the pinned fixture identity has an unrelated machine lease.
        Xunit.Assert.False(DotnetBuildEnvironmentManager.InspectGoalLease(seed.GoalId).RootExists);
        var kernel = await seed.Repository.LoadGoalsAsync([seed.GoalId]);
        var workspace = OrchestratorWorkspace.ForDirectory(Path.Combine(seed.Root, "in-memory"));
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var memoryOutput = CaptureConsole(() => Xunit.Assert.True(CliCommandDispatcher.ExecuteCommand(
            Parts(seed, command), kernel, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal)));

        var result = Transition(seed, command);

        Xunit.Assert.Null(result.Error);
        Xunit.Assert.True(result.Changed);
        var expected = command switch
        {
            "unpark" => MainUnparkOutput,
            "supersede" => MainSupersedeOutput,
            _ => MainAbandonOutput
        };
        Xunit.Assert.Equal(expected.ReplaceLineEndings(Environment.NewLine), result.Output);
        Xunit.Assert.Equal(memoryOutput, result.Output);
        AssertOneLine(seed, EventType(command));
        Xunit.Assert.Equal(CommittedStatus(command), (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
    }

    [Xunit.Theory]
    [Xunit.InlineData("abandon")]
    [Xunit.InlineData("stop-abandon")]
    public async Task AlreadyTerminalAbandon_DoesNotEnqueueOrProjectLifecycleEvent(string command)
    {
        using var seed = await Seed.Create(command, GoalStatus.Cancelled);
        Xunit.Assert.False(DotnetBuildEnvironmentManager.InspectGoalLease(seed.GoalId).RootExists);

        var result = Transition(seed, command);

        Xunit.Assert.Null(result.Error);
        Xunit.Assert.True(result.Changed);
        Xunit.Assert.Equal(MainAbandonOutput.ReplaceLineEndings(Environment.NewLine), result.Output);
        Xunit.Assert.False(File.Exists(seed.EventPath));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
    }
}
