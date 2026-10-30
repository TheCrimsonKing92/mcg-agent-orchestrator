using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
public sealed class GoalDrainPlanTextViewCharacterizationTests
{
    [Fact]
    public void Print_ScheduledItemsAndAppliedActions_PreservesExactText()
    {
        var policy = new GoalDrainPolicy("daytime", 2, ["Developer", "Tester"],
            ["OpenAI", "Anthropic"], "require-confirmation", true, true,
            [new(new TimeOnly(9, 0), new TimeOnly(12, 0)),
             new(new TimeOnly(13, 0), new TimeOnly(17, 0))]);
        var plan = new GoalDrainPlan("safe", policy, Apply: true,
            new(true, "inside allowed window", null), 3, 2, 1, 1, 2,
            [new("abcdef12", "Start work", "subscription", true, false,
                "Ready to start.", "start abcdef12"),
             new("01234567", "Accept work", "acceptance", false, true,
                "Confirmation needed.", "acceptance 01234567")]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalDrainPlanTextView.PrintGoalDrainPlan(plan, ["Started abcdef12", "Queued acceptance"]));

        Assert.Equal(string.Join(Environment.NewLine,
            "Goal drain: policy=safe; apply=True",
            "Drain policy: daytime; maxStarts=2; roles=Developer,Tester; providers=OpenAI,Anthropic; largePrompts=require-confirmation; readinessRiskConfirmation=True; acceptanceGate=True; windows=09:00-12:00,13:00-17:00",
            "Schedule: open; inside allowed window",
            "Ready subscription goals: 3; first batch: 2; acceptance ready: 1; gates: 1; safe supervisor actions: 2",
            "Applied actions: 2",
            "  - Started abcdef12",
            "  - Queued acceptance",
            "Items:",
            "  subscription abcdef12: canApply=True; gate=False",
            "    Ready to start.",
            "    command: start abcdef12",
            "  acceptance 01234567: canApply=False; gate=True",
            "    Confirmation needed.",
            "    command: acceptance 01234567",
            ""), actual);
    }

    [Fact]
    public void Print_NoItemsOrConfiguredWindows_PreservesExactText()
    {
        var policy = new GoalDrainPolicy("unrestricted", 1, ["*"], ["*"], "allow", false, false);
        var plan = new GoalDrainPlan("preview", policy, Apply: false,
            new(false, "drain paused", null), 0, 0, 0, 0, 0, []);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalDrainPlanTextView.PrintGoalDrainPlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "Goal drain: policy=preview; apply=False",
            "Drain policy: unrestricted; maxStarts=1; roles=*; providers=*; largePrompts=allow; readinessRiskConfirmation=False; acceptanceGate=False; windows=always",
            "Schedule: closed; drain paused",
            "Ready subscription goals: 0; first batch: 0; acceptance ready: 0; gates: 0; safe supervisor actions: 0",
            "Items:",
            "  none",
            ""), actual);
    }

    [Fact]
    public void Print_EmptyActionsAndWindows_PreservesExactText()
    {
        var policy = new GoalDrainPolicy("empty", 0, [], [], "block", false, true, []);
        var plan = new GoalDrainPlan("preview", policy, Apply: true,
            new(true, "no windows configured", null), 0, 0, 0, 0, 0, []);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalDrainPlanTextView.PrintGoalDrainPlan(plan, []));

        Assert.Equal(string.Join(Environment.NewLine,
            "Goal drain: policy=preview; apply=True",
            "Drain policy: empty; maxStarts=0; roles=; providers=; largePrompts=block; readinessRiskConfirmation=False; acceptanceGate=True; windows=always",
            "Schedule: open; no windows configured",
            "Ready subscription goals: 0; first batch: 0; acceptance ready: 0; gates: 0; safe supervisor actions: 0",
            "Applied actions: 0",
            "Items:",
            "  none",
            ""), actual);
    }
}
