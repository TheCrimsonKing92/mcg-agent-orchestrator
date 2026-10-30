using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its temporary workspace/database; model and git are fake.
public sealed class AuthorBriefDraftTargetRevisionTests
{
    [Fact]
    public void Missing_target_Revision_receives_failure_and_receipt_records_it()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var valid = CliAuthorDraftCommandTests.ValidMarkdown;
        var invalid = valid.Replace("Implement the requested slice.", "Update `missing/target.cs`.");
        var failed = Assert.Single(AuthorBriefDraftChecks.Run(invalid,
            CliAuthorDraftCommandTests.Fixture.MainSha, fixture.Repository).Where(check => !check.Passed));
        Assert.Equal("modification-targets", failed.Name);
        var requests = new List<WorkerProcessRunRequest>();
        var responses = new[] { Draft(invalid), Draft(valid) };

        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace, new((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(responses[Math.Min(requests.Count - 1, responses.Length - 1)]);
        }, fixture.Repository), fixture.Output, fixture.Error);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(2, requests.Count);
        Assert.Contains($"Failed modification-targets: {failed.Detail}", requests[1].StandardInput!);
        Assert.Equal(valid, File.ReadAllText(outcome.DraftPath!));
        Assert.All(outcome.Checks, check => Assert.True(check.Passed, check.Detail));
        using var receipt = fixture.Receipt();
        Assert.Equal(2, receipt.RootElement.GetProperty("roundCount").GetInt32());
        var rounds = receipt.RootElement.GetProperty("rounds").EnumerateArray().ToArray();
        Assert.Equal(2, rounds.Length);
        Assert.True(rounds[0].GetProperty("checksRun").GetBoolean());
        Assert.Equal(new[] { "modification-targets" }, rounds[0].GetProperty("failedChecks")
            .EnumerateArray().Select(check => check.GetString()).ToArray());
        Assert.Empty(rounds[1].GetProperty("failedChecks").EnumerateArray());
    }

    private static WorkerProcessRunResult Draft(string markdown) =>
        new(0, JsonSerializer.Serialize(new { kind = "draft", markdown }), "");
}
