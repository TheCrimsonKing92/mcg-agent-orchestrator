using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its files and SQLite store; no shared repository or model process.
internal sealed class BoardFillAssessmentTestFixture : IDisposable
{
    internal const string Head = "cccccccccccccccccccccccccccccccccccccccc";
    internal const string Brief = """
        ## Measured premise
        - `src/Feature/File.cs:1` declares the feature.
        ## What to build
        Extend `src/Feature/File.cs`.
        ## Acceptance criteria
        1. The feature works. Developer owns; Acceptance executes. TEST-VERIFIABLE.
        ## Scope
        `src/Feature/File.cs`
        """;
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "board-fill-assessment-" + Guid.NewGuid().ToString("N"));
    internal PanelTestHarness.Clock Clock { get; } = new() { UtcNow = BoardFillReadyItemSelectorTests.Now };
    internal AgentOrchestratorKernel Kernel { get; }
    internal BacklogItem Item { get; set; } = BoardFillReadyItemSelectorTests.Item();
    internal ConductorBoardFillDraftStore Store { get; }
    internal string DraftPath => Path.Combine(Root, "draft.md");
    internal string EventPath => Path.Combine(Root, "events.log");
    private ConductorBoardFillHost? _host;
    internal static BacklogReadiness Ready(BacklogItem _) => new(true, false, "");
    internal static BoardFillPremiseVerification Verified => new("complete", 1, [new(1, "verified", "src/Feature/File.cs:1")]);
    internal static BoardFillScopeProposal NoDepends => new("no-overlap-detected", []);

    internal BoardFillAssessmentTestFixture()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(DraftPath, Brief);
        Kernel = new(Clock);
        Store = new(Path.Combine(Root, "board-fill.db"));
    }

    internal AuthorBriefDraftOutcome Draft() => new("draft", 0, Head, DraftPath, null,
        [new("sections", true, "ok"), new("criteria-present", true, "ok"),
         new("owner-sentence", true, "ok"), new("premise-citations", true, "ok")]);

    internal BoardFillDraftRound Finished(DateTimeOffset? at = null, BacklogItem? item = null)
    {
        var round = Store.Begin(item ?? Item, at ?? Clock.UtcNow);
        Store.Finish(round, Draft(), at ?? Clock.UtcNow);
        return Store.ReadAll().Single(row => row.Id == round.Id);
    }

    internal ConductorBoardFillHost Host(IBoardFillPremiseVerifier verifier) => _host = new(Store, (_, _) => Draft(),
        () => [Item], (_, _) => Ready, () => ConductorAutonomyPolicy.Permissive with { BoardFillMaxDraftsPerDay = 1 },
        new(EventPath, utcNow: () => Clock.UtcNow), () => Clock.UtcNow, verifier);

    internal sealed class Verifier(Func<string, string, CancellationToken, Task<BoardFillPremiseVerification>> run) : IBoardFillPremiseVerifier
    {
        public Task<BoardFillPremiseVerification> VerifyAsync(string premise, string head, CancellationToken token) => run(premise, head, token);
    }

    internal sealed class Repository : IAuthorBriefDraftRepository
    {
        internal string Head { get; set; } = BoardFillAssessmentTestFixture.Head;
        internal int? Lines { get; set; } = 10;
        public string ResolveMainHead() => Head;
        public int? TrackedLineCount(string sha, string path) => path == "src/Feature/File.cs" ? Lines : null;
    }

    public void Dispose()
    {
        _host?.Stop();
        Directory.Delete(Root, recursive: true);
    }
}
