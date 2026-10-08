using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ConductorBoardFillHostNoWriteTests : CliTaskQueryTestSupport
{
    [Fact]
    public async Task Full_draft_cycle_preserves_backlog_goals_and_state_repository()
    {
        // Unique root and SQLite files. The model and repository seams are entirely in memory.
        var root = CreateTempDirectory();
        ConductorBoardFillHost? host = null;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var backlog = new BacklogStore(workspace.BacklogStorePath);
            var item = await backlog.AddAsync("Read only board", "Draft the existing item");
            await backlog.AppendNoteAsync(item.Id, "Read the exact source");
            var clock = new Clock();
            var seed = new AgentOrchestratorKernel(clock);
            var existing = seed.CreateGoal("Existing goal");
            var probe = new ProbeStateRepository(seed);
            var kernel = await probe.LoadGoalsAsync([existing.Id]);
            var beforeGoals = JsonSerializer.Serialize(kernel.ExportSnapshot());
            var beforeBacklog = File.ReadAllBytes(workspace.BacklogStorePath);
            var drafts = new ConductorBoardFillDraftStore(Path.Combine(workspace.OrchestratorDirectory, "board-fill.db"));
            var events = Path.Combine(workspace.OrchestratorDirectory, "conduct-events.log");
            var repository = new Repository();
            var markdown = """
                # Read-only draft
                ## Measured premise
                Main HEAD: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                The receipt seam exists at `docs/role-capability-matrix.md:1`.
                ## What to build
                Draft the receipt.
                ## Acceptance criteria
                1. The receipt records the result. Developer owns; Acceptance executes. TEST-VERIFIABLE.
                2. The Developer reports `tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added. The Tester's evidence_request runs them. Developer owns; Acceptance executes. TEST-VERIFIABLE.
                ## Scope
                Drafting only.
                """;
            var calls = 0;
            var seams = new AuthorBriefDraftSeams((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new WorkerProcessRunResult(0,
                    JsonSerializer.Serialize(new { kind = "draft", markdown }), ""));
            }, repository);
            host = new(drafts, (id, token) => AuthorBriefDraftService.Run(id, workspace, seams,
                TextWriter.Null, TextWriter.Null, token, "board-fill-drafts", () => clock.UtcNow),
                () => BoardFillBacklogSnapshot.Read(workspace.BacklogStorePath),
                (_, _) => candidate => BacklogDependencyReadiness.Evaluate(candidate,
                    _ => null, _ => null, _ => new(null), _ => "Running"),
                () => ConductorAutonomyPolicy.Permissive with { BoardFillMaxDraftsPerDay = 1 },
                new(events, utcNow: () => clock.UtcNow), () => clock.UtcNow);
            host.ServiceTick(kernel);
            await Signal(host.CurrentRound!, "read-only draft finished");
            Assert.Null(Assert.Single(drafts.ReadAll()).Outcome);
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            host.ServiceTick(kernel);
            var round = Assert.Single(drafts.ReadAll());
            Assert.Equal("draft", round.Outcome);
            Assert.All(round.Checks, check => Assert.True(check.Passed, check.Detail));
            Assert.Single(round.Checks, check => check.Name == "post-landing-criterion");
            Assert.True(File.Exists(round.DraftPath));
            Assert.True(File.Exists(round.ReceiptPath));
            Assert.Equal(1, calls);
            Assert.Single(File.ReadAllLines(events));
            Assert.Equal(beforeBacklog, File.ReadAllBytes(workspace.BacklogStorePath));
            Assert.Equal(beforeGoals, JsonSerializer.Serialize(kernel.ExportSnapshot()));
            Assert.Equal(existing.Id, Assert.Single(kernel.Goals).Id);
            Assert.False(File.Exists(workspace.SqliteStatePath));
            Assert.Equal(0, probe.SaveAttempts);
            Assert.Equal(0, probe.MergeSaveAttempts);
            Assert.Equal(0, probe.MutationAttempts);
            Assert.Equal(0, probe.OutboxClaimAttempts);
            Assert.Equal(0, probe.ListOutboxMessagesCount);
        }
        finally
        {
            host?.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task Signal(Task task, string expected)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new InvalidOperationException("Signal did not occur: " + expected); }
    }
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
    private sealed class Repository : IAuthorBriefDraftRepository
    {
        public string ResolveMainHead() => new('a', 40);
        public int? TrackedLineCount(string sha, string path) => path == "docs/role-capability-matrix.md" ? 100 : null;
    }
}
