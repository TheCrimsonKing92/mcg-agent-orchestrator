using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class SubscriptionPlanTextViewCharacterizationTests
{
    [Fact]
    public void PrintRepresentativePlanPreservesExactText()
    {
        var retryAfter = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var readyItem = new SubscriptionPlanItem(
            TaskNumber: 1,
            TaskId: "task-1",
            Role: AgentRole.Developer,
            TaskStatus: WorkTaskStatus.Assigned,
            Description: "Extract subscription output",
            AgentId: "agent-1",
            AgentName: "Sol developer",
            ProviderName: "OpenAI",
            ModelName: "gpt-5-mini",
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            ProfileName: "codex-cli",
            SubscriptionModelAlias: "mini",
            ProfileExists: true,
            ProfileIsResolvable: true,
            ProfileIsEchoOnly: false,
            ProfileIsPatchCapable: true,
            CanPrepare: true,
            Detail: "Ready to prepare",
            TaskComplexity: TaskComplexity.Complex,
            SubscriptionModelName: "gpt-5-mini",
            SubscriptionReasoningEffort: "medium",
            EstimatedPromptCharacterCount: 8500,
            TaskBriefCharacterBudget: 10000,
            TaskBriefHeadroom: 1500,
            Route: new WorkerRouteDecision(
                WorkerRouteDisposition.Selected,
                "Use the selected subscription profile",
                ["Profile exists", "Executable resolves", "Profile can patch"],
                ["Use a smaller model"]));
        var overBudgetItem = readyItem with
        {
            TaskNumber = 2,
            TaskId = "task-2",
            Role = AgentRole.Tester,
            Description = "Verify subscription output",
            AgentId = "agent-2",
            AgentName = "Opus tester",
            ProviderName = "Anthropic",
            ModelName = "claude-opus-5",
            ProfileName = "claude-cli",
            SubscriptionModelAlias = null,
            SubscriptionModelName = null,
            SubscriptionReasoningEffort = null,
            TaskComplexity = null,
            EstimatedPromptCharacterCount = 9100,
            TaskBriefCharacterBudget = 8000,
            TaskBriefHeadroom = -1100,
            CanPrepare = false,
            Detail = "Brief exceeds budget",
            Route = null
        };
        var unassignedItem = new SubscriptionPlanItem(
            3, "task-3", AgentRole.Reviewer, WorkTaskStatus.Pending, "Review the extraction",
            null, null, null, null, null, null, null,
            false, false, false, false, false, "Assign a reviewer");
        var plan = new SubscriptionPlan(
            GoalId: "5935c27081914782a9b8da3b8fd856d5",
            Objective: "Move subscription-plan output into its own view",
            Status: GoalStatus.Active,
            ReadyToPrepareCount: 1,
            ResolvableProfileCount: 2,
            RetryDeferredCount: 1,
            NextSubscriptionRetryAfter: retryAfter,
            ReadyStartCostRisk: "potentially paid",
            ReadyStartPromptCharacterCount: 8500,
            ReadyStartCostRiskDetails: ["Large prompt.", "Paid provider.", "Omitted risk detail."],
            ReadyStartCostRecommendation: "Review provider pricing.",
            CapacitySchedule: new ProviderCapacitySchedule(
                ProviderCapacityDisposition.Deferred, "Wait for provider capacity", 1, 1, retryAfter, true,
                [
                    new ProviderCapacityAction(2, "task-2", "OpenAI", ProviderCapacityDisposition.Deferred,
                        retryAfter, "Retry after cooldown", ["Wait for reset", "Switch provider", "Omitted alternative"]),
                    new ProviderCapacityAction(3, "task-3", null, ProviderCapacityDisposition.Blocked,
                        null, "Assign a profile", [])
                ]),
            ReadyModelUsage:
            [
                new SubscriptionPlanModelSummary(
                    "OpenAI", "gpt-5-mini", 1, TaskComplexity.Complex, "medium", true, 8500,
                    PreviousModelFitNoteCount: 4,
                    PreviousAdequateCount: 1,
                    PreviousOverkillCount: 2,
                    PreviousUnderpoweredCount: 0,
                    PreviousUnknownFitCount: 1,
                    PreviousTaskShapes: ["copy-only change", "docs"],
                    ModelFitRecommendation: "try a smaller model"),
                new SubscriptionPlanModelSummary("Anthropic", "claude-opus-5", 2)
            ],
            ProviderBudgets:
            [
                new SubscriptionProviderBudgetSummary("OpenAI", 2, 1, 1, 2, true, retryAfter, 60, 2, "Cooling down"),
                new SubscriptionProviderBudgetSummary("Anthropic", 1, 1, 0, 0, false, null, null, null, "Available")
            ],
            Items: [readyItem, overBudgetItem, unassignedItem]);

        // Derived from ConsoleViews.Configuration.cs:194-334 before the extraction.
        var expected = string.Join("\n",
            "",
            "Goal 5935c270 subscription plan: ready=1 resolvable=2/3",
            "Objective: Move subscription-plan output into its own view",
            "Status: Active",
            "Ready model usage: OpenAI/gpt-5-mini (Complex) reasoning medium potentially paid: 1 ready task, est prompt 8500 chars, prior fit 4: adequate 1, overkill 2, unknown 1; shapes copy-only change, docs; try a smaller model; Anthropic/claude-opus-5: 2 ready tasks",
            "Provider budgets: OpenAI: ready 1/2, deferred 1, limit failures 2, coolingDown until 2026-10-09 12:00:00Z from task 2; Anthropic: ready 1/1, deferred 0, no cooldown",
            "Capacity schedule: Deferred; ready 1, deferred 1. Wait for provider capacity",
            "  capacity task 2: Deferred provider=OpenAI retryAfter=2026-10-09 12:00:00Z; Retry after cooldown",
            "    alternative: Wait for reset",
            "    alternative: Switch provider",
            "  capacity task 3: Blocked provider=none; Assign a profile",
            "Ready start risk: potentially paid; 8500 prompt chars. Inspect this plan before using --confirm-large-paid-subscription-start. Review provider pricing. Large prompt. Paid provider.",
            "  1. [Assigned] Developer: Extract subscription output",
            "     agent: Sol developer (OpenAI/gpt-5-mini, Complex, SubscriptionOnly)",
            "     subscription: codex-cli model=gpt-5-mini reasoning=medium estPrompt=8500chars budget=10000chars headroom=1500chars profile=True executable=True patchCapable=True",
            "     route: Selected; Use the selected subscription profile",
            "       reason: Profile exists",
            "       reason: Executable resolves",
            "       reason: Profile can patch",
            "       alternative: Use a smaller model",
            "     ready: True; Ready to prepare",
            "  2. [Assigned] Tester: Verify subscription output",
            "     agent: Opus tester (Anthropic/claude-opus-5, SubscriptionOnly)",
            "     subscription: claude-cli model=default estPrompt=9100chars budget=8000chars over=1100chars profile=True executable=True patchCapable=True",
            "     ready: False; Brief exceeds budget",
            "  3. [Pending] Reviewer: Review the extraction",
            "     agent: unassigned",
            "     subscription: none",
            "     ready: False; Assign a reviewer",
            "",
            "");

        var actual = AsyncLocalConsoleRouter.Capture(() => SubscriptionPlanTextView.Print(plan));

        Assert.Equal(expected, actual.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
