using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AuthorBriefDraftRoundCatalogTests
{
    [Theory]
    [InlineData(false, null, "claude --model sonnet --permission-mode plan -p")]
    [InlineData(true, null, "claude --model claude-sonnet-5 --permission-mode plan -p")]
    [InlineData(true, "high", "claude --model claude-sonnet-5 --effort high --permission-mode plan -p")]
    public async Task Catalog_selects_Author_alias_and_preserves_flags(bool bound, string? effort, string expected)
    {
        WorkerProcessRunRequest? request = null;
        var result = await AuthorBriefDraftRound.DispatchAsync("prompt", "root", (value, _) =>
        {
            request = value;
            return Task.FromResult(new WorkerProcessRunResult(0, "draft", ""));
        }, bound ? ConductorRoundModelResolverTests.Catalog(ModelFunctionPurposes.ConductorAuthor, effort) : ModelFunctionCatalog.Empty);
        Assert.Equal("draft", result.StandardOutput);
        Assert.NotNull(request);
        Assert.Equal(expected, request.Command);
        Assert.Equal(TimeSpan.FromMinutes(10), request.Timeout);
        Assert.Equal("prompt", request.StandardInput);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("other-profile")]
    public void Invalid_binding_never_launches_and_cli_records_failure(string shape)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var calls = 0;
        var seams = new AuthorBriefDraftSeams((_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(0, "", ""));
        }, fixture.Repository, ConductorRoundModelResolverTests.InvalidCatalog(ModelFunctionPurposes.ConductorAuthor, shape));
        Assert.Equal(1, CliAuthorDraftCommand.Run(["author-draft", fixture.Item.Id], fixture.Workspace, seams, fixture.Output, fixture.Error));
        Assert.Equal(0, calls);
        using var receipt = fixture.Receipt();
        Assert.StartsWith("model-binding-invalid:conductor-author", receipt.RootElement.GetProperty("failure").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("exitCode").ValueKind);
        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
        Assert.Contains("model-binding-invalid:conductor-author", fixture.Error.ToString());
    }

    [Fact]
    public async Task Invalid_binding_is_rejected_inside_draft_dispatch()
    {
        var calls = 0;
        var exception = await Assert.ThrowsAsync<ConductorModelRoundException>(() => AuthorBriefDraftRound.DispatchAsync(
            "prompt", "root", (_, _) =>
            {
                calls++;
                return Task.FromResult(new WorkerProcessRunResult(0, "", ""));
            }, ConductorRoundModelResolverTests.InvalidCatalog(ModelFunctionPurposes.ConductorAuthor, "duplicate")));
        Assert.Equal(0, calls);
        Assert.StartsWith("model-binding-invalid:conductor-author", exception.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Failed_exit_or_empty_output_records_the_used_alias(int exitCode)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        WorkerProcessRunRequest? request = null;
        var seams = new AuthorBriefDraftSeams((value, _) =>
        {
            request = value;
            return Task.FromResult(new WorkerProcessRunResult(exitCode, "", "bad model"));
        }, fixture.Repository, ConductorRoundModelResolverTests.Catalog(ModelFunctionPurposes.ConductorAuthor));
        Assert.Equal(1, CliAuthorDraftCommand.Run(["author-draft", fixture.Item.Id], fixture.Workspace, seams, fixture.Output, fixture.Error));
        Assert.NotNull(request);
        Assert.Contains("--model claude-sonnet-5", request.Command);
        using var receipt = fixture.Receipt();
        Assert.Equal("claude-sonnet-5", receipt.RootElement.GetProperty("model").GetString());
        Assert.Equal(exitCode, receipt.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
    }
}
