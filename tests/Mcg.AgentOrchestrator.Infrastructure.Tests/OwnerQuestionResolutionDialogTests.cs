using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: each scene owns its stores, lifecycle data and uninitialized GUI application.
public sealed class OwnerQuestionResolutionDialogTests
{
    [Fact(DisplayName = "Resolved activity opens the applied Author answer and subsequent stage")]
    public async Task ResolvedActivityShowsAppliedAnswerAndNextStage()
    {
        using var scene = new Scene();
        var question = "Which direction?\nKeep all the question context.";
        var answer = "Use the documented direction.\nKeep the complete answer.";
        var id = await scene.ResolveSpec(question, answer, Scene.At);
        scene.Tail.Lines.Add(Scene.Lifecycle(Scene.At.AddSeconds(20), "TaskDispatched", scene.Goal.Tasks[0].Id.Value));
        var item = scene.Activity(id, question, Scene.At);
        using IApplication app = Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, scene.Controller, () => Task.CompletedTask);
        view.Render(new(new(true, 1, 0, 0, null, 0), [], [], [item]));
        await view.HandleKeyAsync(Key.Tab); // BOARD
        await view.HandleKeyAsync(Key.Tab); // ACTIVITY
        await view.HandleKeyAsync(Key.Enter);
        Assert.Contains("11111111", Assert.Single(view.ActivityLines));
        var text = Assert.Single(scene.Dialogs.Texts).Text;
        Assert.Contains("Goal: 11111111 " + scene.Goal.Objective, text);
        Assert.Contains("spec clarification before Planner dispatch", text);
        Assert.Contains(question, text);
        Assert.Contains("the Author at " + Scene.Local(Scene.At), text);
        Assert.Contains(answer, text);
        Assert.Contains("docs/decision.md", text);
        Assert.Contains("receipt:42", text);
        Assert.Contains("Planner started at " + Scene.Local(Scene.At.AddSeconds(20)), text);
    }

    [Fact(DisplayName = "Dismissed questions retain goal and stage without inventing an answer")]
    public async Task DismissedQuestionHasNoAnswerSection()
    {
        using var scene = new Scene();
        var request = scene.Harness.Kernel.RequestHumanInput(scene.Goal.Id, scene.Goal.Tasks[0].Id,
            "Should we wait?", isDismissible: true);
        scene.Harness.Kernel.DismissHumanInput(request.Id);
        await scene.Controller.ShowActivityMeaningAsync(scene.Activity(request.Id.Value, request.Question, request.AnsweredAt!.Value));
        var text = Assert.Single(scene.Dialogs.Texts).Text;
        Assert.Contains("Dismissed", text);
        Assert.Contains("11111111 " + scene.Goal.Objective, text);
        Assert.Contains("Planner clarification on task " + OwnerConsoleViewModelBuilder.Prefix(scene.Goal.Tasks[0].Id.Value), text);
        Assert.DoesNotContain("Answer:", text);
        Assert.DoesNotContain("Evidence:", text);
        Assert.Contains("not yet resumed", text);
    }

    [Theory(DisplayName = "Pending, rejected and malformed intents never appear as applied answers")]
    [InlineData(OperatorIntentStatus.Pending, false)]
    [InlineData(OperatorIntentStatus.Rejected, false)]
    [InlineData(OperatorIntentStatus.Applied, true)]
    public async Task InvalidAnswerIntentIsNotAnAnswer(OperatorIntentStatus status, bool malformed)
    {
        using var scene = new Scene();
        var id = await scene.ResolveSpec("Which path?", "Unapplied text", Scene.At, status: status, malformed: malformed);
        await scene.Controller.ShowActivityMeaningAsync(scene.Activity(id, "Which path?", Scene.At));
        var text = Assert.Single(scene.Dialogs.Texts).Text;
        Assert.DoesNotContain("Answer:", text);
        Assert.DoesNotContain("Unapplied text", text);
    }

    [Fact(DisplayName = "A superseded clarification is identified without displaying an answer")]
    public async Task SupersededClarificationHasNoAnswerSection()
    {
        using var scene = new Scene();
        var store = new CollaborationItemStore(Path.Combine(scene.DirectoryPath, "collaboration-items.db"));
        var item = await store.RaiseAsync(CollaborationItemType.Clarification, scene.Goal.Id.Value,
            "Old question", "Question: Old direction?", "spec-clarification:old");
        await store.TryResolveAsync(item.CorrelationKey!, "Superseded by a replacement question");
        var resolution = Assert.Single(await scene.Reader.ListForGoalAsync(scene.Goal.Id.Value, CancellationToken.None));
        await scene.Controller.ShowActivityMeaningAsync(scene.Activity(item.Id, resolution.Question, resolution.ResolvedAt));
        var text = Assert.Single(scene.Dialogs.Texts).Text;
        Assert.Contains("Superseded", text);
        Assert.Contains("spec clarification before Planner dispatch", text);
        Assert.DoesNotContain("Answer:", text);
    }

    [Fact(DisplayName = "The latest applied answer wins while subsequent pending answers remain hidden")]
    public async Task LatestAppliedAnswerWins()
    {
        using var scene = new Scene();
        var id = await scene.ResolveSpec("Which path?", "Old answer", Scene.At);
        await scene.RecordAnswer(id, "Replacement answer", Scene.At.AddSeconds(5));
        await scene.RecordAnswer(id, "Pending answer", Scene.At.AddSeconds(10), status: OperatorIntentStatus.Pending);
        var entry = Assert.Single(await scene.Reader.ListForGoalAsync(scene.Goal.Id.Value, CancellationToken.None));
        Assert.Equal("Replacement answer", entry.Answer);
        Assert.Equal(Scene.At.AddSeconds(5), entry.ResolvedAt);
    }

    internal sealed class Scene : IDisposable
    {
        internal static readonly DateTimeOffset At = new(2026, 1, 1, 0, 2, 0, TimeSpan.Zero);
        internal readonly OwnerConsoleHarness Harness = new();
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "owner-resolution-" + Guid.NewGuid().ToString("N"));
        internal readonly Goal Goal;
        internal readonly TailSource Tail = new();
        internal readonly CapturingDialogs Dialogs = new();
        internal readonly OwnerQuestionResolutionReader Reader;
        internal readonly OwnerConsoleScreenController Controller;
        internal Scene(string? title = null)
        {
            Goal = Harness.AddGoal("11111111-resolution", title ?? "Resolved console question", AgentRole.Planner);
            Directory.CreateDirectory(DirectoryPath);
            Reader = new(Harness.State, Tail, DirectoryPath, DirectoryPath, Harness.Clock);
            Controller = new(Harness.Questions, Harness.Answers, Dialogs, Harness.State, Tail, Harness.Conductor,
                Harness.DigestReport, Harness.Digest, Harness.Clock, resolutions: Reader);
        }

        internal async Task<string> ResolveSpec(string question, string answer, DateTimeOffset at,
            string actor = "author", OperatorIntentStatus status = OperatorIntentStatus.Applied, bool malformed = false)
        {
            var collaboration = new CollaborationItemStore(Path.Combine(DirectoryPath, "collaboration-items.db"));
            var correlation = "spec-clarification:" + Guid.NewGuid().ToString("N");
            var item = await collaboration.RaiseAsync(CollaborationItemType.Clarification, Goal.Id.Value,
                "Clarification", "Question: " + question + "\nBlast radius: goal", correlation);
            await collaboration.TryResolveAsync(correlation, "Closed by answer");
            await RecordAnswer(item.Id, answer, at, actor, status, malformed);
            return item.Id;
        }

        internal async Task RecordAnswer(string targetId, string answer, DateTimeOffset at,
            string actor = "author", OperatorIntentStatus status = OperatorIntentStatus.Applied, bool malformed = false)
        {
            var store = SqliteOperatorIntentStore.ForDirectories(DirectoryPath, DirectoryPath);
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.Clarification, targetId,
                Goal.Id.Value, answer, OperatorActorKind.Agent, ["docs/decision.md", "receipt:42"]);
            var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                OperatorIntentVerbs.Answer, Goal.Id.Value, null, malformed ? "{bad" : JsonSerializer.Serialize(payload),
                [], actor, "conductor-author", "agent", at.AddSeconds(-1), ActorKind: OperatorActorKind.Agent);
            await store.EnqueueAsync(intent);
            if (status != OperatorIntentStatus.Pending)
            {
                Assert.NotNull(await store.ClaimNextAsync(Goal.Id.Value, "test"));
                await store.CompleteAsync(intent.Id, "test", status, "Answer processed", at);
            }
            Harness.Kernel.RecordOperatorIntentApplied(Goal.Id, intent.Id, intent.Verb, null, actor,
                intent.Channel, "agent", "Applied answer", OperatorActorKind.Agent);
        }

        internal OwnerConsoleActivityItem Activity(string id, string text, DateTimeOffset at) =>
            OwnerActivityNarrator.Narrate([], _ => Goal.Objective, attention:
                [new(new(id, Goal.Id.Value, OwnerQuestionKind.Clarification, text), at.AddSeconds(-1), at)])
                .Single(item => item.Kind == "owner-question-resolved");
        internal static string Lifecycle(DateTimeOffset time, string type, string task) =>
            JsonSerializer.Serialize(new { timestamp = time, eventType = type, taskId = task });
        internal static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss");
        public void Dispose() { Controller.Dispose(); SharedTestSupport.RemoveTempDirectory(DirectoryPath); }
    }

    internal sealed class TailSource : IGoalEventTail
    {
        internal List<string> Lines = [];
        public IReadOnlyList<string> ReadLast(string goalId, int count) => Lines.TakeLast(count).ToArray();
    }

    internal sealed class CapturingDialogs : IOwnerConsoleDialogs
    {
        internal readonly List<(string Title, string Text)> Texts = [];
        internal Func<string, IReadOnlyList<int>, int?>? OnPage;
        public Task ShowTextAsync(string title, string text) { Texts.Add((title, text)); return Task.CompletedTask; }
        public Task<int?> ShowPageAsync(string title, string text, IReadOnlyList<int>? choiceLines = null)
        { Texts.Add((title, text)); return Task.FromResult(OnPage?.Invoke(text, choiceLines ?? [])); }
        public Task<bool> ConfirmAsync(string title, string text) => throw new NotSupportedException();
        public Task<string?> PromptTextAsync(string title, string text) => throw new NotSupportedException();
    }
}
