using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliOwnerDigestCommandLessonsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyOrAbsentLessons_PreserveDigestOutputWithoutCreatingOrWriting(bool setup)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.OperatorLessonsStorePath;
        Assert.False(File.Exists(path));
        if (setup) SqliteOperatorLessonStore.Setup(path);
        var bytes = setup ? File.ReadAllBytes(path) : null;
        var modified = setup ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
        using var output = new StringWriter();

        CliOwnerDigestLessons.WriteText(output, fixture.Workspace, OwnerDigestTestFixture.Start, OwnerDigestTestFixture.End);

        Assert.Equal(setup ? $"Lessons: recorded=0 retired=0{Environment.NewLine}" : "", output.ToString());
        Assert.Equal(setup, File.Exists(path));
        if (setup)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task DigestCountsLessonsWithinWindowAndListsRecordedRules()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var workspace = fixture.Workspace;
        var root = workspace.RootDirectory;
        var proof = Path.Combine(root, "lesson-proof.txt");
        File.WriteAllText(proof, "proof");
        File.Delete(Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName));
        var now = OwnerDigestTestFixture.Start.AddHours(2);
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var lessons = new SqliteOperatorLessonStore(workspace.OperatorLessonsStorePath);
        var coordinator = new OperatorIntentCoordinator(intents, utcNow: () => now)
        {
            Lessons = new OperatorLessonIntentServices(lessons,
                new AdjudicationEvidenceResolver(workspace.OrchestratorDirectory), _ => null)
        };
        async Task<string> Enqueue(string verb, object payload)
        {
            var id = Guid.NewGuid().ToString("N");
            await intents.EnqueueAsync(new OperatorIntentRecord(id, id, verb, OperatorIntentScopes.Workspace,
                null, System.Text.Json.JsonSerializer.Serialize(payload, payload.GetType(), OperatorIntentJson.Options),
                [], "tester", "cli", "local-process", now));
            coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel());
            return id;
        }
        var record = await Enqueue(OperatorIntentVerbs.LessonRecord,
            new LessonRecordOperatorIntentPayload("Situation", "Rule in digest", ["operator-evidence:" + proof],
                ["test"], null, root));
        now = now.AddMinutes(30);
        var longRule = new string('R', 85);
        var second = await Enqueue(OperatorIntentVerbs.LessonRecord,
            new LessonRecordOperatorIntentPayload("Second situation", longRule,
                ["operator-evidence:" + proof], ["test"], null, root));
        now = now.AddHours(1);
        await Enqueue(OperatorIntentVerbs.LessonRetire,
            new LessonRetireOperatorIntentPayload(record, "Corrected", [], root));
        now = OwnerDigestTestFixture.End.AddHours(1);
        await Enqueue(OperatorIntentVerbs.LessonRecord,
            new LessonRecordOperatorIntentPayload("Later", "Outside window", ["operator-evidence:" + proof],
                ["test"], null, root));

        using var output = new StringWriter();
        Assert.Equal(0, CliOwnerDigestCommand.Run(["owner-digest", "--since",
            OwnerDigestTestFixture.Start.ToString("O"), "--until", OwnerDigestTestFixture.End.ToString("O")],
            workspace, fixture.Clock, output));
        Assert.Contains("Lessons: recorded=2 retired=1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"{record} | Rule in digest", output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"{second} | {longRule[..79]}…", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(longRule, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Outside window", output.ToString(), StringComparison.Ordinal);
    }
}
