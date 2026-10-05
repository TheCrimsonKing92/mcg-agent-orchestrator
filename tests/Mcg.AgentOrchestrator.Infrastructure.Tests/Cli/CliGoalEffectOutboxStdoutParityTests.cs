using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalEffectOutboxStdoutParityTests : CliGoalEffectOutboxTestSupport
{
    // Constants transcribed from main 18438ee33's renderers and fixed seed; the abandon
    // constant is also pinned in CliGoalLifecycleOutboxStdoutParityTests from step 5a.
    private const string MainParkOutput =
        "Goal parked aaaaaaaa.\nCancelled running dispatches: 0\nResolved human waits: 0\nResolved attention items: 1\n";
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
    [Xunit.InlineData("park")]
    [Xunit.InlineData("stop-park")]
    [Xunit.InlineData("abandon")]
    [Xunit.InlineData("stop-abandon")]
    public async Task ConfirmedSuccess_MatchesMainBytes_LeavesNoPendingEffects(string command)
    {
        using var seed = await Seed.Create();
        Xunit.Assert.False(DotnetBuildEnvironmentManager.InspectGoalLease(seed.GoalId).RootExists);
        await Raise(seed);

        var result = EffectTransition(seed, command);

        Xunit.Assert.Null(result.Error);
        Xunit.Assert.True(result.Changed);
        var park = command is "park" or "stop-park";
        Xunit.Assert.Equal((park ? MainParkOutput : MainAbandonOutput).ReplaceLineEndings(Environment.NewLine), result.Output);
        Xunit.Assert.Equal(park ? GoalStatus.Parked : GoalStatus.Cancelled,
            (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(ParkKind));
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(AbandonKind));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
    }
}
