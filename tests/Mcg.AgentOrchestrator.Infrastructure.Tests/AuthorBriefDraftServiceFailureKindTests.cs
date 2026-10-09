using System.ComponentModel;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fake process/repository seams and a unique receipt/backlog root per test.
public sealed class AuthorBriefDraftServiceFailureKindTests
{
    [Fact]
    public void Repository_failure_is_pre_model_without_entering_process_seam()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        fixture.Repository.Head = () => throw new InvalidOperationException("repository unavailable");
        var calls = 0;
        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace,
            new((_, _) => { calls++; throw new Exception("unexpected model call"); }, fixture.Repository),
            fixture.Output, fixture.Error);
        Assert.Equal(0, calls);
        AssertFailure(fixture, outcome, BoardFillFailureKind.PreModel);
    }

    [Theory]
    [InlineData("launch", "pre-model")]
    [InlineData("timeout", "model-round")]
    [InlineData("exit", "model-round")]
    [InlineData("output", "model-round")]
    public void Model_faults_record_the_failure_kind_in_outcome_and_receipt(string fault, string expected)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var calls = 0;
        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace,
            new((_, _) =>
            {
                calls++;
                return fault switch
                {
                    "launch" => throw new Win32Exception(2, "executable missing"),
                    "timeout" => Task.FromException<WorkerProcessRunResult>(new OperationCanceledException("model timed out")),
                    "exit" => Task.FromResult(new WorkerProcessRunResult(1, "", "model crashed")),
                    _ => Task.FromResult(new WorkerProcessRunResult(0, "unusable output", ""))
                };
            }, fixture.Repository), fixture.Output, fixture.Error);
        Assert.Equal(1, calls);
        AssertFailure(fixture, outcome, expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repository_fault_after_model_response_counts_as_a_model_round(bool nativeFault)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var resolutions = 0;
        fixture.Repository.Head = () =>
        {
            if (++resolutions == 1) return CliAuthorDraftCommandTests.Fixture.MainSha;
            if (nativeFault) throw new Win32Exception(5, "repository native fault");
            throw new InvalidOperationException("tracked tree changed after model start");
        };
        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace,
            new((_, _) => Task.FromResult(new WorkerProcessRunResult(0,
                JsonSerializer.Serialize(new { kind = "stale", reason = "already done",
                    evidenceReferences = new[] { "seed.txt:1" } }), "")), fixture.Repository),
            fixture.Output, fixture.Error);
        Assert.Equal(2, resolutions);
        AssertFailure(fixture, outcome, BoardFillFailureKind.ModelRound);
    }

    private static void AssertFailure(CliAuthorDraftCommandTests.Fixture fixture,
        AuthorBriefDraftOutcome outcome, string expected)
    {
        Assert.Equal("failed", outcome.Kind);
        Assert.Equal(expected, outcome.FailureKind);
        using var receipt = fixture.Receipt();
        Assert.Equal(expected, receipt.RootElement.GetProperty("failureKind").GetString());
    }
}
