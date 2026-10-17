using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SshRemoteLaneExecutorTestsHostBudget
{
    [Theory]
    [InlineData(2100)]
    [InlineData(null)]
    public async Task Submit_OptionalBudget_PreservesExistingJobFields(int? budget)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var request = FakeSshRemoteLaneTransport.Request with { TestHostTimeoutSeconds = budget };
        var submission = await fake.Executor().SubmitAsync(request, default);
        fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        var copy = Assert.Single(fake.Calls.Where(call => call.Arguments.Contains("job.json")));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(copy.Directory, "job.json")));
        var job = json.RootElement;
        Assert.Equal(budget.HasValue ? 10 : 9, job.EnumerateObject().Count());
        var expected = new Dictionary<string, string>
        {
            ["sha"] = request.VerifyingCommitSha, ["project"] = request.Project, ["filter"] = request.Filter,
            ["lane"] = request.Lane, ["attemptId"] = request.AttemptId, ["executorId"] = request.ExecutorId,
            ["filterHash"] = request.FilterHash, ["mainSha"] = request.MainSha, ["manifestIdentity"] = request.ManifestIdentity
        };
        foreach (var field in expected) Assert.Equal(field.Value, job.GetProperty(field.Key).GetString());
        if (budget.HasValue)
        {
            Assert.Equal("testHostTimeoutSeconds", job.EnumerateObject().Last().Name);
            Assert.Equal(JsonValueKind.Number, job.GetProperty("testHostTimeoutSeconds").ValueKind);
            Assert.Equal(budget.Value, job.GetProperty("testHostTimeoutSeconds").GetInt32());
        }
        else Assert.False(job.TryGetProperty("testHostTimeoutSeconds", out _));
    }
}
