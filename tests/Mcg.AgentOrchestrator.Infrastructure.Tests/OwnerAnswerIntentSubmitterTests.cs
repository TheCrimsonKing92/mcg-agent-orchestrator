using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns a unique directory and a real SQLite intent store.
public sealed class OwnerAnswerIntentSubmitterTests
{
    [Fact]
    public void Submit_Clarification_EnqueuesAttributedAnswerVerbatim()
    {
        using var scene = new Scene();
        var question = scene.Question(OwnerQuestionKind.Clarification);
        const string answer = " Keep this answer \n";
        const string key = " supplied-key ";

        var submitted = new OwnerAnswerIntentSubmitter(scene.Workspace).Submit(question, answer, key);

        var intent = Assert.Single(scene.ReadIntents());
        Assert.Equal(submitted.IntentId, intent.Id);
        Assert.True(Guid.TryParseExact(intent.Id, "N", out _));
        Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb);
        Assert.Equal(question.GoalId, intent.GoalId);
        Assert.Null(intent.TaskId);
        Assert.Equal(key, intent.IdempotencyKey);
        Assert.Equal("operator", intent.Actor);
        Assert.Equal("owner-console", intent.Channel);
        Assert.Equal("local-process", intent.AuthenticationAssurance);
        Assert.Equal(OperatorActorKind.Human, intent.ActorKind);
        Assert.Empty(intent.PayloadFileReferences);
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        var payload = ReadPayload(intent);
        Assert.Equal(new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.Clarification,
                question.ItemId, question.GoalId, answer, OperatorActorKind.Human, null, null),
            payload with { EvidenceReferences = null });
        Assert.NotNull(payload.EvidenceReferences);
        Assert.Empty(payload.EvidenceReferences);
        Assert.Null(payload.Precedent);
    }

    [Fact]
    public void Submit_HumanInput_PreservesFullTargetAndGoal()
    {
        using var scene = new Scene();
        var question = scene.Question(OwnerQuestionKind.HumanInput);

        new OwnerAnswerIntentSubmitter(scene.Workspace).Submit(question, "answer", "human-key");

        var payload = ReadPayload(Assert.Single(scene.ReadIntents()));
        Assert.Equal(OperatorAnswerTargetKind.HumanInput, payload.TargetKind);
        Assert.Equal(question.ItemId, payload.TargetId);
        Assert.Equal(question.GoalId, payload.GoalId);
    }

    [Fact]
    public void Submit_ReusedKey_DeduplicatesAndRejectsConflictingAnswer()
    {
        using var scene = new Scene();
        var question = scene.Question(OwnerQuestionKind.Clarification);
        var submitter = new OwnerAnswerIntentSubmitter(scene.Workspace);

        var first = submitter.Submit(question, "answer", "same-key");
        var second = submitter.Submit(question, "answer", "same-key");

        Assert.Equal(first.IntentId, second.IntentId);
        Assert.Equal(first.IntentId, Assert.Single(scene.ReadIntents()).Id);
        Assert.Throws<InvalidOperationException>(() => submitter.Submit(question, "different", "same-key"));
        var remaining = Assert.Single(scene.ReadIntents());
        Assert.Equal(first.IntentId, remaining.Id);
        Assert.Equal("answer", ReadPayload(remaining).Text);
    }

    [Fact]
    public void Submit_ViewOnlyKinds_ThrowsWithoutEnqueueing()
    {
        using var scene = new Scene();
        var submitter = new OwnerAnswerIntentSubmitter(scene.Workspace);

        Assert.Throws<ArgumentException>(() => submitter.Submit(
            scene.Question(OwnerQuestionKind.StewardHold), "answer", "steward-key"));
        Assert.Throws<ArgumentException>(() => submitter.Submit(
            scene.Question(OwnerQuestionKind.ExperimentReading), "answer", "experiment-key"));

        Assert.Empty(scene.ReadIntents());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    public void Submit_BlankAnswer_ThrowsWithoutEnqueueing(string? answer)
    {
        using var scene = new Scene();
        Assert.Throws<ArgumentException>(() => new OwnerAnswerIntentSubmitter(scene.Workspace)
            .Submit(scene.Question(OwnerQuestionKind.Clarification), answer!, "key"));
        Assert.Empty(scene.ReadIntents());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    public void Submit_BlankKey_ThrowsWithoutEnqueueing(string? key)
    {
        using var scene = new Scene();
        Assert.Throws<ArgumentException>(() => new OwnerAnswerIntentSubmitter(scene.Workspace)
            .Submit(scene.Question(OwnerQuestionKind.Clarification), "answer", key!));
        Assert.Empty(scene.ReadIntents());
    }

    [Fact]
    public void AdapterSubmit_WithoutCatalogOrProfiles_EnqueuesConsoleIntent()
    {
        using var scene = new Scene();
        Assert.False(File.Exists(scene.Workspace.AgentCatalogPath));
        Assert.False(File.Exists(scene.Workspace.WorkerProfilePath));
        var question = scene.Question(OwnerQuestionKind.Clarification);

        var submitted = new AttentionAnswerHandlerAdapter(scene.Workspace).Submit(question, "answer");

        var intent = Assert.Single(scene.ReadIntents());
        Assert.Equal(submitted.IntentId, intent.Id);
        Assert.Equal("owner-console", intent.Channel);
        Assert.True(Guid.TryParseExact(intent.IdempotencyKey, "N", out _));
    }

    private static AnswerOperatorIntentPayload ReadPayload(OperatorIntentRecord intent) =>
        JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;

    private sealed class Scene : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "owner-answer-" + Guid.NewGuid().ToString("N"));
        private readonly string _goalId = Guid.NewGuid().ToString("N");
        internal OrchestratorWorkspace Workspace { get; }

        internal Scene()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
        }

        internal OwnerQuestion Question(OwnerQuestionKind kind) =>
            new(Guid.NewGuid().ToString("N"), _goalId, kind, "Question?");

        internal IReadOnlyList<OperatorIntentRecord> ReadIntents() =>
            SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory)
                .ListForGoalAsync(_goalId).GetAwaiter().GetResult();

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
