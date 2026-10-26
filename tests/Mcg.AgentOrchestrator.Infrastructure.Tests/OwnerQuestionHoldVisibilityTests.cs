using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all state and rendering are local to each test.
public sealed class OwnerQuestionHoldVisibilityTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private const string Question = "Which repair? Keep question= and evidence=[inside] intact.";
    private const string Identity = "steward-owner-question:fixture-trigger-key";

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Active)]
    [Xunit.InlineData(GoalStatus.Failed)]
    public void Steward_hold_is_visible_and_blocks_next_before_cleanup(GoalStatus status)
    {
        var goal = CreateGoal(status);
        using var bare = JsonDocument.Parse(AttentionAgentReadView.Render([], [], goals: [goal]));
        var row = Xunit.Assert.Single(bare.RootElement.GetProperty("payload")
            .GetProperty("ownerQuestionHolds").EnumerateArray());
        Xunit.Assert.Equal(new[] { "goalId", "state", "identity", "question", "taskId", "taskNumber",
            "startedAt", "resolvingVerb", "nextAction" }, row.EnumerateObject().Select(field => field.Name));
        Xunit.Assert.Equal(goal.Id.Value, row.GetProperty("goalId").GetString());
        Xunit.Assert.Equal("steward-owner-question", row.GetProperty("state").GetString());
        Xunit.Assert.Equal(Identity, row.GetProperty("identity").GetString());
        Xunit.Assert.Equal(Question, row.GetProperty("question").GetString());
        Xunit.Assert.Equal(goal.Tasks[1].Id.Value, row.GetProperty("taskId").GetString());
        Xunit.Assert.Equal(2, row.GetProperty("taskNumber").GetInt32());
        Xunit.Assert.Equal(Started.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            row.GetProperty("startedAt").GetString());
        Xunit.Assert.Equal("retry", row.GetProperty("resolvingVerb").GetString());
        var command = $"retry --goal {goal.Id.Value[..8]} 2 --cause <cause> --text-file <path>";
        Xunit.Assert.Equal(command, row.GetProperty("nextAction").GetString());

        var disposition = new GoalOperatorDispositionSurface().Evaluate(goal, 0, false);
        Xunit.Assert.Equal(OperatorDispositionState.Blocked, disposition.State);
        Xunit.Assert.Equal(OperatorDispositionConfidence.High, disposition.Confidence);
        Xunit.Assert.Equal(new[] { "steward-owner-question" }, disposition.Blockers);
        Xunit.Assert.Equal(command, disposition.NextSafeCommand);
        Xunit.Assert.Equal($"steward-owner-question hold excludes this goal from every conductor walk: {Question}",
            disposition.Reason);
        var evidence = Xunit.Assert.Single(disposition.Evidence);
        Xunit.Assert.Equal("goal-hold", evidence.Kind);
        Xunit.Assert.Equal(Identity, evidence.Path);
        Xunit.Assert.Equal($"started={Started.ToString("O", CultureInfo.InvariantCulture)}", evidence.Detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData("HumanInput")]
    [Xunit.InlineData("Clarification")]
    public void Author_target_emits_answer_command_and_show_filters_by_goal(string kind)
    {
        const string target = "11111111111111111111111111111111";
        var author = CreateGoal(GoalStatus.Active, "author-owner-question",
            $"author-owner-question item={kind}:{target} reason=model-ask-owner question={Question} recommendation=Use the repair");
        var steward = CreateGoal(GoalStatus.Active);
        using var bare = JsonDocument.Parse(AttentionAgentReadView.Render([], [], goals: [steward, author]));
        var rows = bare.RootElement.GetProperty("payload").GetProperty("ownerQuestionHolds").EnumerateArray().ToArray();
        Xunit.Assert.Equal(new[] { steward.Id.Value, author.Id.Value }, rows.Select(row => row.GetProperty("goalId").GetString()));
        var row = rows[1];
        Xunit.Assert.Equal("answer", row.GetProperty("resolvingVerb").GetString());
        Xunit.Assert.Equal(Question, row.GetProperty("question").GetString());
        Xunit.Assert.Equal(JsonValueKind.Null, row.GetProperty("taskId").ValueKind);
        Xunit.Assert.Equal(JsonValueKind.Null, row.GetProperty("taskNumber").ValueKind);
        Xunit.Assert.Equal(kind == "HumanInput" ? $"answer {target} --text-file <path>" :
            $"attention answer {author.Id.Value[..8]} {target} --text-file <path>", row.GetProperty("nextAction").GetString());

        using var show = JsonDocument.Parse(AttentionShowAgentReadView.Render(author.Id.Value, [], [], _ => null,
            goals: [steward, author]));
        var shown = Xunit.Assert.Single(show.RootElement.GetProperty("payload").GetProperty("ownerQuestionHolds").EnumerateArray());
        Xunit.Assert.Equal(row.GetRawText(), shown.GetRawText());
        using var all = JsonDocument.Parse(AttentionShowAgentReadView.Render(null, [], [], _ => null,
            goals: [steward, author]));
        Xunit.Assert.Equal(2, all.RootElement.GetProperty("payload").GetProperty("ownerQuestionHolds").GetArrayLength());
    }

    [Xunit.Theory]
    [Xunit.InlineData("steward-owner-question", "unparseable steward blocker", "retry")]
    [Xunit.InlineData("steward-owner-question", "steward-owner-question trigger=a:b::d question=Repair? evidence=[]", "retry")]
    [Xunit.InlineData("author-owner-question", "author-owner-question item=bad", "answer")]
    [Xunit.InlineData("author-owner-question", "author-owner-question item=Unknown:target reason=missing-evidence question=Repair? recommendation=Repair", "answer")]
    public void Malformed_owner_hold_remains_visible_with_inspection_command(string state, string blocker, string verb)
    {
        var goal = CreateGoal(GoalStatus.Active, state, blocker);
        using var json = JsonDocument.Parse(AttentionAgentReadView.Render([], [], goals: [goal]));
        var row = Xunit.Assert.Single(json.RootElement.GetProperty("payload").GetProperty("ownerQuestionHolds").EnumerateArray());
        Xunit.Assert.Equal(blocker, row.GetProperty("question").GetString());
        Xunit.Assert.Equal(JsonValueKind.Null, row.GetProperty("taskId").ValueKind);
        Xunit.Assert.Equal(JsonValueKind.Null, row.GetProperty("taskNumber").ValueKind);
        Xunit.Assert.Equal(verb, row.GetProperty("resolvingVerb").GetString());
        Xunit.Assert.Equal($"attention show {goal.Id.Value[..8]}", row.GetProperty("nextAction").GetString());
        Xunit.Assert.Equal(new[] { state }, new GoalOperatorDispositionSurface().Evaluate(goal, 0, false).Blockers);
    }

    [Xunit.Fact]
    public void Missing_task_keeps_parsed_question_and_retry_placeholder()
    {
        var missing = TaskId.New().Value;
        var goal = CreateGoal(GoalStatus.Active, blocker:
            $"steward-owner-question case=A trigger=goal:{missing}:sha:Kind question={Question} evidence=[]");
        var hold = Xunit.Assert.IsType<OwnerQuestionHoldBlocker>(OwnerQuestionHoldBlocker.TryRead(goal));
        Xunit.Assert.Equal(Question, hold.Question);
        Xunit.Assert.Equal(missing, hold.TaskId);
        Xunit.Assert.Null(hold.TaskNumber);
        Xunit.Assert.Equal($"retry --goal {goal.Id.Value[..8]} <task-number> --cause <cause> --text-file <path>", hold.NextAction);
    }

    [Xunit.Fact]
    public void Other_holds_and_finished_goals_are_hidden_in_both_views()
    {
        Goal[] goals = [CreateGoal(GoalStatus.Active, state: null), CreateGoal(GoalStatus.Active, "dependency"),
            CreateGoal(GoalStatus.Completed), CreateGoal(GoalStatus.Cancelled), CreateGoal(GoalStatus.Superseded)];
        using var bare = JsonDocument.Parse(AttentionAgentReadView.Render([], [], goals: goals));
        using var show = JsonDocument.Parse(AttentionShowAgentReadView.Render(null, [], [], _ => null, goals: goals));
        Xunit.Assert.Empty(bare.RootElement.GetProperty("payload").GetProperty("ownerQuestionHolds").EnumerateArray());
        Xunit.Assert.Empty(show.RootElement.GetProperty("payload").GetProperty("ownerQuestionHolds").EnumerateArray());
        foreach (var goal in goals)
            Xunit.Assert.DoesNotContain("steward-owner-question", new GoalOperatorDispositionSurface().Evaluate(goal, 0, false).Blockers);
    }

    [Xunit.Fact]
    public void Removing_hold_restores_baseline_disposition()
    {
        var kernel = CreateKernel(GoalStatus.Active, state: null);
        var goal = Xunit.Assert.Single(kernel.Goals);
        var surface = new GoalOperatorDispositionSurface();
        var baseline = surface.Evaluate(goal, 0, false);
        kernel.ObserveGoalHold(goal.Id, "steward-owner-question",
            $"steward-owner-question case=A trigger={goal.Id.Value}:{goal.Tasks[1].Id.Value}:sha:DeveloperNoChangeWithConfirmedRed question={Question} evidence=[receipt]",
            Started, TimeSpan.MaxValue, Identity);
        Xunit.Assert.Equal(OperatorDispositionState.Blocked, surface.Evaluate(goal, 0, false).State);
        kernel.ClearGoalHold(goal.Id);
        var actual = surface.Evaluate(goal, 0, false);
        Xunit.Assert.Equal(baseline.State, actual.State);
        Xunit.Assert.Equal(baseline.Blockers, actual.Blockers);
        Xunit.Assert.Equal(baseline.NextSafeCommand, actual.NextSafeCommand);
    }

    private static Goal CreateGoal(GoalStatus status, string? state = "steward-owner-question", string? blocker = null)
        => Xunit.Assert.Single(CreateKernel(status, state, blocker).Goals);

    private static AgentOrchestratorKernel CreateKernel(GoalStatus status, string? state, string? blocker = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Owner question visibility",
            [new TaskSpec(TaskId.New(), "First task", AgentRole.Planner), new TaskSpec(TaskId.New(), "Trigger task", AgentRole.Developer)]);
        blocker ??= $"steward-owner-question case=A trigger={goal.Id.Value}:{goal.Tasks[1].Id.Value}:sha:DeveloperNoChangeWithConfirmedRed question={Question} evidence=[receipt]";
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(item => item with
            {
                Status = status,
                CurrentHold = state is null ? null : new GoalHoldSnapshot(Identity, state, blocker, Started)
            }).ToArray()
        });
    }
}
