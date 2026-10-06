using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its backlog/receipt root, with fake repository and process seams.
public sealed class AuthorBriefDraftServiceHeldTests
{
    [Fact]
    public void Repository_hold_carries_observed_main_separately_from_successful_main()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var exception = Mismatch("head-not-main");
        fixture.Repository.Head = () => throw exception;
        var modelCalls = 0;

        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace,
            new((_, _) =>
            {
                modelCalls++;
                return Task.FromResult(new WorkerProcessRunResult(0, "{}", ""));
            }, fixture.Repository), fixture.Output, fixture.Error);

        Assert.Equal("held", outcome.Kind);
        Assert.Equal(exception.Main, outcome.HeldMainHead);
        Assert.Null(outcome.MainHead);
        Assert.Equal(0, modelCalls);
        using var receipt = fixture.Receipt();
        Assert.False(receipt.RootElement.TryGetProperty("heldMainHead", out _));
    }

    [Theory]
    [InlineData("head-not-main")]
    [InlineData("toplevel-not-root")]
    [InlineData("head-not-main, toplevel-not-root")]
    public void Repository_mismatch_before_dispatch_is_held_with_observed_values(string condition)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var exception = Mismatch(condition);
        fixture.Repository.Head = () => throw exception;
        var modelCalls = 0;

        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace,
            new((_, _) =>
            {
                modelCalls++;
                return Task.FromResult(new WorkerProcessRunResult(0, "{}", ""));
            }, fixture.Repository), fixture.Output, fixture.Error);

        Assert.Equal("held", outcome.Kind);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Null(outcome.MainHead);
        Assert.Equal(0, modelCalls);
        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
        using var receipt = fixture.Receipt();
        Assert.Equal("held", receipt.RootElement.GetProperty("kind").GetString());
        var failure = receipt.RootElement.GetProperty("failure").GetString();
        Assert.Equal($"AuthorDraftRepositoryNotAtMainException: {exception.Message}", failure);
        Assert.Equal(failure, outcome.Failure);
        Assert.Contains(condition, failure);
        Assert.Contains(exception.Head, failure);
        Assert.Contains(exception.Main, failure);
        Assert.Contains(exception.Toplevel, failure);
        Assert.Contains(exception.ConfiguredRoot, failure);
    }

    [Fact]
    public void Tracked_edits_remain_failed_without_dispatch()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        const string message = "Author drafting requires no tracked edits against main HEAD.";
        fixture.Repository.Head = () => throw new InvalidOperationException(message);
        var modelCalls = 0;

        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace,
            new((_, _) =>
            {
                modelCalls++;
                return Task.FromResult(new WorkerProcessRunResult(0, "{}", ""));
            }, fixture.Repository), fixture.Output, fixture.Error);

        Assert.Equal("failed", outcome.Kind);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(0, modelCalls);
        using var receipt = fixture.Receipt();
        Assert.Equal("failed", receipt.RootElement.GetProperty("kind").GetString());
        Assert.Equal($"InvalidOperationException: {message}", receipt.RootElement.GetProperty("failure").GetString());
    }

    [Fact]
    public void Repository_mismatch_after_dispatch_remains_failed()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var resolutions = 0;
        var exception = Mismatch("head-not-main");
        fixture.Repository.Head = () => ++resolutions == 1 ? CliAuthorDraftCommandTests.Fixture.MainSha : throw exception;
        var modelCalls = 0;

        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace,
            new((_, _) =>
            {
                modelCalls++;
                return Task.FromResult(new WorkerProcessRunResult(0,
                    JsonSerializer.Serialize(new { kind = "draft", markdown = CliAuthorDraftCommandTests.ValidMarkdown }), ""));
            }, fixture.Repository), fixture.Output, fixture.Error);

        Assert.Equal("failed", outcome.Kind);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(CliAuthorDraftCommandTests.Fixture.MainSha, outcome.MainHead);
        Assert.Equal(1, modelCalls);
        Assert.Equal(2, resolutions);
        using var receipt = fixture.Receipt();
        Assert.Equal("failed", receipt.RootElement.GetProperty("kind").GetString());
        Assert.Equal($"AuthorDraftRepositoryNotAtMainException: {exception.Message}",
            receipt.RootElement.GetProperty("failure").GetString());
    }

    private static AuthorDraftRepositoryNotAtMainException Mismatch(string condition) =>
        new(condition, new string('b', 40), CliAuthorDraftCommandTests.Fixture.MainSha,
            "observed-toplevel", "configured-root");
}
