using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: Capture routes output through AsyncLocal; fixtures have no shared state.
public sealed class GoalTextViewCharacterizationTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void PrintGoal_DeclaredRemovals_DoesNotWarnAboutCriterionCount()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Implement behavior.\n## Acceptance criteria\n" +
            "1. Behavior works.\n2. test-removal: FooTests.Removed");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Implement behavior.",
            ["Behavior works."], VerificationClass.TestVerifiable, [], []));

        var lines = CaptureLines(() => GoalTextView.PrintGoal(goal));

        Assert.Contains("Brief acceptance criteria (1):", lines);
        Assert.Contains("Refined acceptance criteria (1):", lines);
        Assert.DoesNotContain(lines, line => line.Contains("WARNING:", StringComparison.Ordinal));
        Assert.DoesNotContain("  2. test-removal: FooTests.Removed", lines);
    }

    // Literal formats derived from ConsoleViews.GoalsAndTasks.cs at 09a304658.
    [Fact]
    public void PrintGoal_MinimalGoal_PreservesFullAndTasksOnlyLineSequences()
    {
        var goal = Fixture();
        var agent = goal.Tasks[0].AssignedAgentId!.Value[..8];
        Assert.Equal(GoalPriority.Normal, goal.Priority);
        Assert.Null(goal.SourceBacklogItemId);
        Assert.Empty(goal.EffectiveAcceptanceCriteriaCorrections);
        Assert.Empty(goal.CriterionEvidenceObligations);
        Assert.Empty(AcceptanceCriteriaParser.ParseDeclared(goal.Objective));

        Assert.Equal(new[]
        {
            "",
            "Goal goal0001aaaa",
            "Objective: Characterize goal output",
            "Status: Active",
            "Tasks:",
            $"  1. [Assigned] Developer: Render goal details ({agent})",
            "  2. [Pending] Tester: Verify goal details (unassigned)",
            ""
        }, CaptureLines(() => GoalTextView.PrintGoal(goal)));

        Assert.Equal(new[]
        {
            "",
            "Goal goal0001aaaa (Label)",
            "Status: Custom",
            "Tasks:",
            $"  1. [Assigned] Developer: Render goal details ({agent})",
            "  2. [Pending] Tester: Verify goal details (unassigned)",
            ""
        }, CaptureLines(() => GoalTextView.PrintGoal(goal, "Label", "Custom", tasksOnly: true)));
    }

    [Fact]
    public void PrintGoal_BacklogPortfolioAndDeclaredCriteria_PreservesDetailOrder()
    {
        var goal = Fixture(details: true);
        Assert.Null(goal.RefinedSpec);
        var lines = CaptureLines(() => GoalTextView.PrintGoal(goal,
            portfolio: new PortfolioMembership("epic0001aaaa", "Epic A", null, null)));
        var statusIndex = Array.IndexOf(lines, "Status: Active");
        var tasksIndex = Array.IndexOf(lines, "Tasks:");

        Assert.True(statusIndex >= 0);
        Assert.True(tasksIndex > statusIndex);
        Assert.Equal(new[]
        {
            "Priority: Critical",
            "Source backlog: backlog-1",
            "Source backlog coverage: slice",
            "Portfolio: epic=Epic A (epic0001) project=unassigned",
            "Brief acceptance criteria (2):",
            "  1. First declared criterion.",
            "  2. Second declared criterion.",
            "Refined acceptance criteria: pending refinement (brief declares 2)"
        }, lines[(statusIndex + 1)..tasksIndex]);
    }

    [Fact]
    public void PrintTaskQueryResult_TwoTasks_PreservesFullLineSequence()
    {
        var goal = Fixture();
        var agent = goal.Tasks[0].AssignedAgentId!.Value[..8];
        var result = new TaskQueryResult(goal.Id, goal.Objective, goal.Status, new TaskQuery(), goal.Tasks);
        Assert.Equal(1, TaskDisplayNumber.Resolve(goal, goal.Tasks[0].Id));
        Assert.Equal(2, TaskDisplayNumber.Resolve(goal, goal.Tasks[1].Id));

        Assert.Equal(new[]
        {
            "",
            "Goal goal0001 Active: Characterize goal output",
            "Tasks matched: 2",
            $"  1. 12345678 [Assigned] Developer: Render goal details ({agent}) evidence=none",
            "  2. 87654321 [Pending] Tester: Verify goal details (unassigned) evidence=none",
            ""
        }, CaptureLines(() => GoalTextView.PrintTaskQueryResult(goal, result)));
    }

    [Theory]
    [InlineData("none", "none")]
    [InlineData("execution", "execution")]
    [InlineData("dispatch", "dispatch")]
    [InlineData("running", "running-process")]
    [InlineData("cancelled", "cancelled-process")]
    [InlineData("completed", "completed-process")]
    [InlineData("verification-pass", "passed-verification")]
    [InlineData("verification-fail", "failed-verification")]
    [InlineData("verification-and-running", "passed-verification")]
    public void FormatTaskEvidence_IsolatedRecordsAndVerificationPrecedence_PreservesLabels(
        string recordKind, string expected)
    {
        var task = Fixture(recordKind: recordKind).Tasks[0];
        Assert.Equal(expected, GoalTextView.FormatTaskEvidence(task));
        if (recordKind == "verification-and-running")
        {
            Assert.True(task.LastVerification!.Succeeded);
            Assert.True(task.LastProcess!.IsRunning);
        }
    }

    [Fact]
    public void PrintGoalIntakeReceipt_PreservesSingleJsonLineAndMemberOrder()
    {
        var record = new GoalIntakeRequestRecord("request-1", 1, "fingerprint-1", "created",
            Start, Start.AddMinutes(1), "goal0001aaaa", null, null, @"C:\work\out.log", @"C:\work\err.log");
        var expected = JsonSerializer.Serialize(new
        {
            kind = "goal-intake-receipt",
            requestKey = record.RequestKey,
            state = record.State,
            createdAt = record.CreatedAt,
            updatedAt = record.UpdatedAt,
            goalId = record.GoalId,
            failureCode = record.FailureCode,
            failureDetail = record.FailureDetail,
            stdoutPath = record.StdoutPath,
            stderrPath = record.StderrPath
        });

        Assert.Equal(new[] { expected }, CaptureLines(() => GoalIntakeReceiptTextView.PrintGoalIntakeReceipt(record)));
    }

    [Fact]
    public void PrintHealth_OneOfEachConfiguration_PreservesFullLineSequence()
    {
        var report = new OrchestratorHealthReport(
            [new ProviderConfigurationStatus("Ollama", true, "local", "provider ready")],
            [new AgentConfigurationValidation(AgentRole.Developer, "Local developer", "Ollama", "qwen-local",
                null, null, AgentExecutionPolicy.ApiOnly, null, null, null, true, "local-worker", true, "agent ready")],
            [new WorkerProfileValidation("local-worker", "worker {prompt}", "worker", true, false, true, false, "profile ready")]);

        Assert.Equal(new[]
        {
            "Ready: True",
            "Providers:",
            "  Ollama: configured=True mode=local (provider ready)",
            "Agents:",
            "  Developer: valid=True execution=ApiOnly api=Ollama/qwen-local reasoning=default subscription=none alternate-ready=True alternates=local-worker (agent ready)",
            "Worker profiles:",
            "  local-worker: executable=worker resolvable=True (profile ready)"
        }, CaptureLines(() => HealthTextView.PrintHealth(report)));
    }

    [Fact]
    public void ExtractedOwners_LeaveNoPartialFileOrConsoleViewsForwarders()
    {
        var cliRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.GoalsAndTasks.cs")));
        foreach (var owner in new[] { "GoalTextView", "GoalIntakeReceiptTextView", "HealthTextView" })
        {
            var source = File.ReadAllText(Path.Combine(cliRoot, owner + ".cs"));
            Assert.Contains("internal static class " + owner, source);
            Assert.DoesNotMatch(@"\bpartial\s+class\b", source);
        }

    }

    [Fact]
    public void ConsoleViewsRatchet_UsesReducedAtOrUnderBudget()
    {
        var ceiling = Assert.Single(SourceSizeRatchet.SeededClassCeilings,
            row => row.ClassName == "ConsoleViews");
        Assert.True(ceiling.MaximumTotalLineCount <= 2234);
        Assert.True(ceiling.MaximumPartialFileCount <= 32);
        Assert.Empty(SourceSizeRatchet.EvaluateClasses(VerifiedRepositoryRoot.Find(), [ceiling]));
    }

    private static Goal Fixture(bool details = false, string recordKind = "none")
    {
        var clock = new FixedClock(Start);
        var kernel = new AgentOrchestratorKernel(clock);
        var objective = details
            ? "Characterize goal output\n\n## Acceptance criteria\n\n1. First declared criterion.\n2. Second declared criterion."
            : "Characterize goal output";
        var goal = kernel.CreateGoal(new GoalId("goal0001aaaa"), objective,
            [new TaskSpec(new TaskId("12345678-dev"), "Render goal details", AgentRole.Developer),
             new TaskSpec(new TaskId("87654321-test"), "Verify goal details", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        var first = goalSnapshot.Tasks[0];
        first = first with
        {
            LastExecution = recordKind == "execution"
                ? new TaskExecutionSnapshot(first.AssignedAgentId!, "Local developer", "Ollama", "qwen-local",
                    "done", "stop", 100, 20, Start.AddSeconds(1)) : null,
            LastDispatch = recordKind == "dispatch"
                ? new TaskDispatchSnapshot("local-worker", "run cmd", @"C:\work", Start.AddSeconds(2)) : null,
            LastProcess = recordKind is "running" or "cancelled" or "completed" or "verification-and-running"
                ? new TaskProcessSnapshot(123, "run cmd", @"C:\work", "out.log", "err.log", "exit.txt", Start.AddSeconds(3),
                    recordKind is "running" or "verification-and-running" ? null : Start.AddSeconds(4),
                    recordKind is "running" or "verification-and-running" ? null : 0, WasCancelled: recordKind == "cancelled") : null,
            LastVerification = recordKind is "verification-pass" or "verification-fail" or "verification-and-running"
                ? new TaskVerificationSnapshot("verify", @"C:\work", recordKind == "verification-fail" ? 1 : 0,
                    "", "", Start.AddSeconds(5)) : null
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with
            {
                Priority = details ? "Critical" : "Normal",
                SourceBacklogItemId = details ? "backlog-1" : null,
                SourceBacklogCoverage = details ? SourceBacklogCoverage.Slice : null,
                Tasks = [first, goalSnapshot.Tasks[1] with { AssignedAgentId = null, Status = WorkTaskStatus.Pending }]
            }]
        }, new FixedClock(Start.AddMinutes(1)));
        return Assert.Single(kernel.Goals);
    }

    private static string[] CaptureLines(Action action)
    {
        var lines = AsyncLocalConsoleRouter.Capture(action).Split(Environment.NewLine);
        // Drop only the terminator's empty element, preserving the printed trailing blank line.
        return lines.Length > 0 && lines[^1] == "" ? lines[..^1] : lines;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
