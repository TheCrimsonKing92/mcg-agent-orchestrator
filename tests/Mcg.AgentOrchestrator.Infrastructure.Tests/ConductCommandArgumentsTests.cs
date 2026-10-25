using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductCommandArgumentsTests
{
    // Parallel-safe: each theory uses only its arguments and pure functions.
    public static TheoryData<string[], bool> LoopCases => new()
    {
        { ["conduct", "--loop"], true },
        { ["conduct", "--watch"], true },
        { ["CONDUCT", "--LOOP"], true },
        { ["conduct", "abc"], false },
        { ["status", "--loop"], false },
        { [], false }
    };

    [Theory]
    [MemberData(nameof(LoopCases))]
    public void IsLoop_Arguments_Matches_cli_and_expected_value(string[] args, bool expected)
    {
        var actual = ConductCommandArguments.IsLoop(args);

        Assert.Equal(expected, actual);
        Assert.Equal(CliPersistentStateRunner.IsConductLoop(args), actual);
    }

    public static TheoryData<string[], int> PollCases => new()
    {
        { ["conduct", "--loop", "--poll-seconds", "7"], 7 },
        { ["conduct", "--poll-seconds=9"], 9 },
        { ["conduct", "--watch-interval", "4"], 4 },
        { ["conduct", "--loop"], ConductorBatchLoop.DefaultWatchIntervalSeconds }
    };

    [Theory]
    [MemberData(nameof(PollCases))]
    public void Poll_seconds_Arguments_Matches_cli_and_expected_value(string[] args, int expected)
    {
        var actual = ConductCommandArguments.ResolvePollSeconds(args);

        Assert.Equal(expected, actual);
        Assert.Equal(CliCommandHandlers.ResolveConductPollSeconds(args), actual);
    }

    public static TheoryData<string[]> InvalidPollCases => new()
    {
        new string[] { "conduct", "--poll-seconds" },
        new string[] { "conduct", "--poll-seconds", "0" },
        new string[] { "conduct", "--watch-interval", "x" }
    };

    [Theory]
    [MemberData(nameof(InvalidPollCases))]
    public void Poll_seconds_Invalid_arguments_Preserves_cli_exception(string[] args)
    {
        var actual = Assert.Throws<ArgumentException>(() => ConductCommandArguments.ResolvePollSeconds(args));
        var cli = Assert.Throws<ArgumentException>(() => CliCommandHandlers.ResolveConductPollSeconds(args));

        Assert.Equal("--poll-seconds requires a positive integer value.", actual.Message);
        Assert.Equal(cli.Message, actual.Message);
    }

    public static TheoryData<string[], bool, bool, bool> StoreSetupCases => new()
    {
        { ["backlog-list"], true, false, false },
        { ["backlog-similar", "query"], true, false, false },
        { ["backlog-show", "abc"], true, false, false },
        { ["backlog-depends", "abc", "--on", "def"], true, false, false },
        { ["backlog-add", "title", "--depends-on", "abc"], true, false, false },
        { ["backlog-add", "title"], true, false, false },
        { ["backlog-add", "title", "--no-similar"], false, false, false },
        { ["progress", "1"], false, true, false },
        { ["retry", "1"], false, true, false },
        { ["verify-manual", "1"], false, true, false },
        { ["ADJUDICATE"], false, true, false },
        { ["note", "1"], false, false, false },
        { ["criterion-evidence-map"], false, false, true },
        { ["criterion-evidence-record"], false, false, true },
        { ["criterion-evidence-repair"], false, false, true },
        { ["goals"], false, false, false },
        { [], false, false, false }
    };

    [Theory]
    [MemberData(nameof(StoreSetupCases))]
    public void Store_setup_Arguments_Matches_cli_and_expected_kinds(
        string[] args, bool requiresBacklog, bool inboxMutation, bool evidenceMutation)
    {
        Assert.Equal(requiresBacklog, StoreSetupCommandKinds.RequiresBacklogState(args));
        Assert.Equal(CliPersistentStateRunner.RequiresKernelBacklogState(args),
            StoreSetupCommandKinds.RequiresBacklogState(args));
        Assert.Equal(inboxMutation, StoreSetupCommandKinds.IsInboxBackedTaskMutation(args));
        Assert.Equal(CliPersistentStateRunner.IsInboxBackedGoalScopedTaskMutationCommand(args),
            StoreSetupCommandKinds.IsInboxBackedTaskMutation(args));
        Assert.Equal(evidenceMutation, StoreSetupCommandKinds.IsCriterionEvidenceMutation(args));
        Assert.Equal(CliPersistentStateRunner.IsCriterionEvidenceMutationCommand(args),
            StoreSetupCommandKinds.IsCriterionEvidenceMutation(args));
    }
}
