using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

[Collection("IsolatedProcessSpawning")]
public sealed class DeferredNoChangeContextDeliveryTestsIsolatedProcessSpawning
{
    [Fact]
    public async Task CompletedRunOnTesterAIsDeliveredByOrdinaryHostPreparationToTesterBAfterReload()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        var first = fixture.Prepare(fixture.TesterA);
        var originalPrompt = File.ReadAllBytes(first.PromptPath);
        var originalFiles = fixture.TesterA.LastDispatch!.ContextPackageReceipt!.Sections
            .Where(section => section.MandatoryRelativePath is not null)
            .ToDictionary(section => section.MandatoryRelativePath!, section => DeferredNoChangeContextFixture.Recover(fixture.Worktree, section));
        fixture.Complete();
        var attempt = fixture.Attempt;
        Assert.Null(attempt.FocusedEvidenceReceiptId); // The observed legacy contract is intentional.
        Assert.Contains(fixture.Goal.Timeline, evt => evt.TaskId == fixture.TesterA.Id && evt.Message.Contains(Uri.EscapeDataString(attempt.ResultPath), StringComparison.Ordinal));
        var testerB = fixture.AddTester();
        await fixture.Reload();
        testerB = fixture.Goal.Tasks.Single(task => task.Id == testerB.Id);

        var dispatch = fixture.Prepare(testerB);

        var receipt = testerB.LastDispatch!.ContextPackageReceipt!;
        var bodySection = Assert.Single(receipt.Sections, section => section.LogicalIdentity.StartsWith("goal/deferred-no-change-receipts/", StringComparison.Ordinal));
        var bytes = DeferredNoChangeContextFixture.Recover(fixture.Worktree, bodySection);
        Assert.Equal(bodySection.ContentHash, WorkerContextArtifact.Hash(bytes));
        Assert.Contains(attempt.AttemptId, bodySection.LogicalIdentity, StringComparison.Ordinal);
        Assert.Contains(bodySection.ContentHash, bodySection.LogicalIdentity, StringComparison.Ordinal);
        var body = JsonSerializer.Deserialize<JsonElement>(bytes);
        Assert.Equal(fixture.Goal.Id.Value, body.GetProperty("GoalId").GetString());
        Assert.Equal(attempt.AttemptId, body.GetProperty("AttemptId").GetString());
        Assert.Equal(fixture.Candidate, body.GetProperty("BranchHeadSha").GetString());
        Assert.Equal("Green", body.GetProperty("Availability").GetString());
        Assert.Equal(1, body.GetProperty("CandidateArm").GetProperty("ExecutedTestCount").GetInt32());
        Assert.Equal("Infrastructure.Tests:DeferredAlphaTests", body.GetProperty("Selections")[0].GetString());
        Assert.False(body.GetProperty("NegativeControlExecuted").GetBoolean());
        var trx = body.GetProperty("Trx")[0];
        Assert.Equal(trx.GetProperty("Sha256").GetString(), WorkerContextArtifact.Hash(
            Convert.FromBase64String(trx.GetProperty("ContentBase64").GetString()!)));
        Assert.Contains(AgentRole.Tester, bodySection.RoleVisibility);
        Assert.Contains(AgentRole.Reviewer, bodySection.RoleVisibility);
        var prompt = File.ReadAllText(dispatch.PromptPath);
        Assert.Contains("identity=goal/deferred-no-change-evidence.json;", prompt, StringComparison.Ordinal);
        Assert.Contains(bodySection.LogicalIdentity, prompt, StringComparison.Ordinal);
        var lines = prompt.Replace("\r\n", "\n").Split('\n');
        var summaryLine = Array.FindIndex(lines, line => line.StartsWith("INLINE FULL: identity=goal/deferred-no-change-evidence.json;", StringComparison.Ordinal));
        Assert.True(summaryLine >= 0, "Ordinary Tester preparation must expose the deferred evidence summary.");
        var summary = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Deserialize<string>(lines[summaryLine + 1])!);
        Assert.Equal(fixture.Candidate, summary.GetProperty("CurrentCandidateSha").GetString());
        Assert.True(summary.GetProperty("Receipts")[0].GetProperty("CurrentGreenProof").GetBoolean());
        Assert.Equal(ContextDeliveryMode.MandatoryFile, bodySection.DeliveryMode);
        var manifestText = prompt[(prompt.IndexOf("INLINE FULL: identity=context/manifest.v1.json;", StringComparison.Ordinal))..];
        Assert.Contains(bodySection.ContentHash, manifestText, StringComparison.Ordinal);
        Assert.Equal(originalPrompt, File.ReadAllBytes(first.PromptPath));
        foreach (var original in originalFiles)
            Assert.Equal(original.Value, File.ReadAllBytes(Path.Combine(fixture.Worktree, original.Key)));
    }
}
