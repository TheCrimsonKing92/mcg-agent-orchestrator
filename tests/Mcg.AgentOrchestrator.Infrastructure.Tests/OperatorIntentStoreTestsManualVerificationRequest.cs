using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorIntentStoreTestsManualVerificationRequest
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RequestUsesOriginalSubmissionTime(bool passed)
    {
        var submitted = DateTimeOffset.Parse("2026-09-09T01:00:00Z");
        var payload = new ManualVerificationOperatorIntentPayload(Request: new(passed, "Observed behavior", "fixture"));
        var restored = JsonSerializer.Deserialize<ManualVerificationOperatorIntentPayload>(JsonSerializer.Serialize(payload))!;
        var verification = restored.ResolveVerification(submitted);
        Xunit.Assert.Equal(submitted, verification.CompletedAt);
        Xunit.Assert.Equal(passed ? 0 : 1, verification.ExitCode);
        Xunit.Assert.Equal("Observed behavior", passed ? verification.AuthoritativeStandardOutput : verification.AuthoritativeStandardError);
    }

    [Xunit.Fact]
    public void LegacyExplicitReceiptKeepsItsObservationTime()
    {
        var observed = DateTimeOffset.Parse("2026-09-08T01:00:00Z");
        var receipt = ManualVerificationRecorder.Create(true, "Historical evidence", "fixture", observed);
        var json = JsonSerializer.Serialize(new ManualVerificationOperatorIntentPayload(receipt));
        Xunit.Assert.DoesNotContain("Request", json, StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<ManualVerificationOperatorIntentPayload>(json)!;
        Xunit.Assert.Equal(observed, restored.ResolveVerification(observed.AddDays(1)).CompletedAt);
        Xunit.Assert.Equal("Historical evidence", restored.ResolveVerification(observed.AddDays(1)).AuthoritativeStandardOutput);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void EmptyOrAmbiguousPayloadIsRejected(bool ambiguous)
    {
        var payload = ambiguous
            ? new ManualVerificationOperatorIntentPayload(
                ManualVerificationRecorder.Create(true, "Receipt", "fixture", DateTimeOffset.UtcNow), new(true, "Request", "fixture"))
            : new ManualVerificationOperatorIntentPayload();
        Xunit.Assert.Throws<InvalidOperationException>(() => payload.ResolveVerification(DateTimeOffset.UtcNow));
    }

    [Xunit.Fact]
    public async Task RequestReplayKeepsFirstSubmissionAndStillRejectsChangedEvidence()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = SqliteOperatorIntentStore.ForDirectories(root, Path.Combine(root, "logs"));
            var submitted = DateTimeOffset.Parse("2026-09-09T01:00:00Z");
            var payload = new ManualVerificationOperatorIntentPayload(Request: new(true, "Original evidence", root));
            var first = new OperatorIntentRecord("first", "request-key", "verify-manual", GoalId.New().Value, TaskId.New().Value,
                JsonSerializer.Serialize(payload), [], "operator", "cli", "local-process", submitted);
            await store.EnqueueAsync(first);
            var replayed = await store.EnqueueAsync(first with { Id = "replayed", CreatedAt = submitted.AddMinutes(1) });
            Xunit.Assert.Equal(first.Id, replayed.Id);
            Xunit.Assert.Equal(submitted, replayed.CreatedAt);
            Xunit.Assert.Equal(submitted, JsonSerializer.Deserialize<ManualVerificationOperatorIntentPayload>(replayed.PayloadJson)!.ResolveVerification(replayed.CreatedAt).CompletedAt);
            var changed = first with { Id = "changed", PayloadJson = JsonSerializer.Serialize(new ManualVerificationOperatorIntentPayload(Request: new(true, "Changed evidence", root))) };
            await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() => store.EnqueueAsync(changed));
            Xunit.Assert.Equal(first.PayloadJson, (await store.GetAsync(first.Id))!.PayloadJson);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
