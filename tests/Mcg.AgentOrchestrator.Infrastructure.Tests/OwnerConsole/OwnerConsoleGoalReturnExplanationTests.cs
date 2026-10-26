using System.Globalization;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: per-test kernels, dialogs and unique temporary logs; no initialized terminal or external stores.
public sealed class OwnerConsoleGoalReturnExplanationTests
{
    private const string Id = "22222222222222222222222222222222";
    private static readonly DateTimeOffset T1 = new(2026, 1, 1, 12, 10, 20, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddMinutes(47).AddSeconds(12);

    [Fact]
    public async Task BoardEnter_ReturnedDeveloper_ShowsOrderedCheckCauseAndFinding()
    {
        var harness = Harness();
        Change(harness, goal =>
        {
            var task = Assert.Single(goal.Tasks);
            return goal with { Tasks =
            [
                Developer(task) with { RetryAdmissionHistory = [Receipt(RetryCause.NewSourceFinding) with
                    { StableFindingIds = ["RV-1"] }] },
                Check(task, AgentRole.Tester, T1.AddMinutes(-1)),
                Check(task, AgentRole.Reviewer, T1) with { VerificationHistory =
                    [Verification(T1) with { ReviewedCommit = "abcdef1234567890", MergedReviewFindings =
                        [Finding("RV-1", "Missing empty-input control\nSecond line detail")] }] }
            ] };
        });

        var text = await BoardText(harness);

        AssertInOrder(text, "Tasks:", "Returned to Developer at " + Local(harness, T2),
            "  Previous check: Reviewer at " + Local(harness, T1) + " on candidate abcdef12",
            "  Cause: NewSourceFinding, route UpstreamImplementation",
            "  Finding RV-1: Missing empty-input control", "Recent events");
        Assert.DoesNotContain("Second line detail", text);
        Assert.DoesNotContain("Reason: not recorded", text);
    }

    [Fact]
    public async Task BoardEnter_MainDriftReturn_ShowsCauseWithoutInventingFinding()
    {
        var harness = Harness();
        Change(harness, goal =>
        {
            var task = Assert.Single(goal.Tasks);
            return goal with { Tasks =
            [
                Developer(task) with { RetryAdmissionHistory = [Receipt(RetryCause.MainDriftConflict)] },
                Check(task, AgentRole.Reviewer, T1)
            ] };
        });

        var text = await BoardText(harness);

        Assert.Contains("Cause: MainDriftConflict, route UpstreamImplementation", text);
        Assert.Contains("Previous check: Reviewer at " + Local(harness, T1), text);
        Assert.DoesNotContain("Finding ", text);
        Assert.DoesNotContain("Reason: not recorded", text);
        Assert.DoesNotContain("Reviewer passed", text);
        Assert.DoesNotContain("Reviewer failed", text);
    }

    [Fact]
    public async Task GoalCommand_MissingCausalState_StatesReasonNotRecorded()
    {
        var harness = Harness();
        Change(harness, goal =>
        {
            var task = Assert.Single(goal.Tasks);
            return goal with { Tasks = [Developer(task), Check(task, AgentRole.Tester, T1)] };
        });

        var text = await CommandText(harness);

        Assert.Contains("Returned to Developer at " + Local(harness, T2), text);
        Assert.Contains("Previous check: Tester at " + Local(harness, T1), text);
        Assert.Contains("Reason: not recorded in goal state", text);
        Assert.DoesNotContain("Cause:", text);
        Assert.DoesNotContain("Finding ", text);
        Assert.DoesNotContain("Criterion feedback:", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BoardEnter_NoEarlierCheck_OmitsReturnBlock(bool firstPass)
    {
        var harness = Harness();
        Change(harness, goal =>
        {
            var task = Assert.Single(goal.Tasks);
            var developer = Developer(task) with { LastDispatch = new("dev", "cmd", ".", T1) };
            return goal with { Tasks = firstPass
                ? [developer, Check(task, AgentRole.Reviewer, T2)] : [developer] };
        });

        var text = await BoardText(harness);

        Assert.Contains("Tasks:", text);
        Assert.DoesNotContain("Returned to Developer", text);
        Assert.DoesNotContain("Previous check:", text);
        Assert.DoesNotContain("Cause:", text);
        Assert.DoesNotContain("Reason: not recorded", text);
    }

    [Fact]
    public async Task GoalCommand_MixedHistory_SelectsRelevantStateAndCapsEvidence()
    {
        var harness = Harness();
        Change(harness, goal =>
        {
            var task = Assert.Single(goal.Tasks);
            var selected = Verification(T1) with { ReviewedCommit = "short", MergedReviewFindings =
            [
                Finding("resolved", "Resolved issue") with { State = ReviewFindingState.Resolved },
                Finding("fallback", "Other open issue"),
                Finding("preferred", "Fix {empty} input\r\nHidden finding detail")
            ] };
            return goal with { Tasks =
            [
                Developer(task) with { LastDispatch = new("other", "cmd", ".", T2.AddMinutes(5)) },
                Check(task, AgentRole.Tester, T1.AddMinutes(-1)),
                Developer(task) with
                {
                    CriterionRetryFeedback = ["Check {empty} input\nHidden feedback detail", "Later feedback"],
                    RetryAdmissionHistory =
                    [
                        Receipt(RetryCause.MainDriftConflict) with { ReceiptId = "latest", RecordedAt = T2.AddMinutes(1),
                            StableFindingIds = ["resolved", "preferred"], EvidenceIdentities = ["ev-a", "ev-a", "ev-c", "ev-d"] },
                        Receipt(RetryCause.NewSourceFinding) with { ReceiptId = "older", RecordedAt = T1 },
                        Receipt(RetryCause.NewTestFinding) with { ReceiptId = "stale", RecordedAt = T1.AddMinutes(-1) }
                    ]
                },
                Check(task, AgentRole.Reviewer, T1) with { VerificationHistory =
                    [selected, Verification(T1.AddMinutes(-2)), Verification(T2)] },
                task with { Id = TaskId.New().Value, LastDispatch = null }
            ] };
        });

        var text = await CommandText(harness);

        AssertInOrder(text, "Returned to Developer at " + Local(harness, T2),
            "Previous check: Reviewer at " + Local(harness, T1) + " on candidate short",
            "Cause: MainDriftConflict, route UpstreamImplementation",
            "Finding preferred: Fix  empty  input", "Criterion feedback: Check  empty  input",
            "Evidence: ev-a, ev-a, ev-c");
        foreach (var absent in new[] { "Finding resolved:", "Finding fallback:", "NewSourceFinding", "NewTestFinding",
                     "Hidden finding detail", "Hidden feedback detail", "Later feedback", "ev-d", "Reason: not recorded", "{", "}" })
            Assert.DoesNotContain(absent, text);
    }

    [Theory]
    [InlineData("Correct {empty} input\nHidden feedback", true)]
    // Braces survive snapshot trimming, then sanitize to an empty first line.
    [InlineData("{}\r\nHidden feedback", false)]
    public async Task GoalCommand_FeedbackWithoutReceipt_UsesOnlyFirstEntryLine(string feedback, bool hasReason)
    {
        var harness = Harness();
        Change(harness, goal =>
        {
            var task = Assert.Single(goal.Tasks);
            return goal with { Tasks =
            [
                Developer(task) with { CriterionRetryFeedback = [feedback, "Later feedback"],
                    RetryAdmissionHistory = [Receipt(RetryCause.NewTestFinding) with { RecordedAt = T1.AddTicks(-1) }] },
                Check(task, AgentRole.Tester, T1)
            ] };
        });

        var restoredDeveloper = Assert.Single(harness.Kernel.ExportSnapshot().Goals).Tasks
            .Single(task => task.RequiredRole == AgentRole.Developer);
        Assert.Equal(feedback, restoredDeveloper.CriterionRetryFeedback[0]);
        var text = await CommandText(harness);

        Assert.Contains("Returned to Developer at", text);
        if (hasReason)
        {
            Assert.Contains("Criterion feedback: Correct  empty  input", text);
            Assert.DoesNotContain("Reason: not recorded", text);
        }
        else
        {
            Assert.DoesNotContain("Criterion feedback:", text);
            Assert.Contains("Reason: not recorded in goal state", text);
        }
        Assert.DoesNotContain("Hidden feedback", text);
        Assert.DoesNotContain("Later feedback", text);
        Assert.DoesNotContain("Cause:", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GoalCommand_LogFallback_IgnoresFileAndUsesRetainedFindings(bool retained)
    {
        var path = Path.Combine(Path.GetTempPath(), "return-detail-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(path, FindingOutput("File-only finding"));
            var harness = Harness();
            Change(harness, goal =>
            {
                var task = Assert.Single(goal.Tasks);
                var check = Verification(T1) with
                {
                    StandardOutput = retained ? FindingOutput("Retained finding") : "",
                    StandardOutputPath = path,
                    MergedReviewFindings = retained ? [Finding("merged", "Merged finding")] : []
                };
                return goal with { Tasks = [Developer(task), Check(task, AgentRole.Reviewer, T1) with
                    { VerificationHistory = [check] }] };
            });

            var text = await CommandText(harness);

            Assert.Contains("Returned to Developer at", text);
            Assert.DoesNotContain("File-only finding", text);
            Assert.DoesNotContain("Merged finding", text);
            if (retained) Assert.Contains("Finding recorded: Retained finding", text);
            else Assert.Contains("Reason: not recorded in goal state", text);
        }
        finally { File.Delete(path); }
    }

    private static string FindingOutput(string description) => "WORKER_RESULT:\nfindings: " +
        System.Text.Json.JsonSerializer.Serialize(new[] { new
        {
            stable_id = "recorded", state = "open", severity = "blocking", category = "correctness",
            location = new { file = "src/example.cs", region = "Example" }, description
        } }) + "\ntouched_anchors: []\nEND_WORKER_RESULT";

    private static OwnerConsoleHarness Harness()
    {
        var harness = new OwnerConsoleHarness(new ConsoleClock());
        harness.AddGoal(Id, "Return goal", AgentRole.Developer);
        return harness;
    }

    private static TaskSnapshot Developer(TaskSnapshot task) => task with
    {
        Id = TaskId.New().Value, RequiredRole = AgentRole.Developer, Status = WorkTaskStatus.Running,
        LastDispatch = new("dev", "cmd", ".", T2)
    };

    private static TaskSnapshot Check(TaskSnapshot task, AgentRole role, DateTimeOffset at) => task with
    {
        Id = TaskId.New().Value, RequiredRole = role, Status = WorkTaskStatus.Completed,
        LastVerification = Verification(at), VerificationHistory = [Verification(at)]
    };

    private static TaskVerificationSnapshot Verification(DateTimeOffset at) => new("check", ".", 0, "", "", at);
    private static ReviewFinding Finding(string id, string description) =>
        new(id, ReviewFindingState.Open, new("src/example.cs", "Example"), description);
    private static RetryAdmissionReceipt Receipt(RetryCause cause) => new("receipt", cause, new(1, "fingerprint"),
        RetryAdmissionDecision.Allowed, RetryAdmissionRoute.UpstreamImplementation, PaidRouteClassification.NonPaid, T2, T2);
    private static string Local(OwnerConsoleHarness harness, DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, harness.Clock.LocalTimeZone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static void Change(OwnerConsoleHarness harness, Func<GoalSnapshot, GoalSnapshot> change)
    {
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(change).ToArray() });
    }

    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness, Dialogs dialogs) =>
        OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs, harness.State, harness.Tail,
            harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);

    private static async Task<string> BoardText(OwnerConsoleHarness harness)
    {
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var model = await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
            new Lookup(), harness.Clock).BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0));
        view.Render(model);
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);
        return Assert.Single(dialogs.Goals).Current.Text;
    }

    private static async Task<string> CommandText(OwnerConsoleHarness harness)
    {
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs);
        await controller.RunCommandAsync(":goal " + Id[..8]);
        return Assert.Single(dialogs.Goals).Current.Text;
    }

    private static void AssertInOrder(string text, params string[] expected)
    {
        var previous = -1;
        foreach (var line in expected)
        {
            var index = text.IndexOf(line, StringComparison.Ordinal);
            Assert.True(index > previous, $"Expected '{line}' after offset {previous}, found at {index}.\n{text}");
            previous = index;
        }
    }

    private sealed class ConsoleClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => T2.AddMinutes(1);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone(
            "Return detail test zone", TimeSpan.FromHours(5.5), "Return detail test zone", "Return detail test zone");
    }

    private sealed class Lookup : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<OwnerConsoleGoalDialog> Goals = [];
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected prompt");
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Expected goal dialog: " + text);
        public async Task ShowGoalAsync(OwnerConsoleGoalDialog dialog)
        {
            Goals.Add(dialog);
            dialog.Start(action => action());
            await dialog.LastLoad;
        }
    }
}
