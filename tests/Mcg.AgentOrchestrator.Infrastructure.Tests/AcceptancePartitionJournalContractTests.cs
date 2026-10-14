using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePartitionJournalContractTests : IDisposable
{
    private static readonly GoalId GoalId = new("981d0d72fa6440f4a2c39711bf6e73ad");
    private const string Candidate = "ec64b6ed8296548f08b390f648152e0453d1a36b";
    private const string Main = "94cda609a28ec61acfe9481f18a1c151cd6b837f";
    private const string Attempt = "981d0d72-0-20261013130557813-1dc5ce3344664bec802614ccb1bf0037";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "partition-journal-" + Guid.NewGuid().ToString("N"));
    private readonly GoalAcceptanceVerifier.AcceptanceManifestCheck _partition = new()
    {
        Name = "infrastructure tests: remainder",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~AcceptancePartitionJournalContractTests"]
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void NativeRetryAndPassingProbe_RoundTripThroughStrictReader_KeepHardRedAndRequireRegate()
    {
        var cache = CreateCache(Attempt);
        var first = FirstRun();
        cache.RecordWithinAttemptRetry(_partition, first, Attempt + ":remainder:0", Attempt + ":remainder:1",
            new AcceptanceRetainedDiagnostic("original-failure.err", new string('a', 64)));
        var selected = cache.SelectPartitionVerdict(_partition, first, PassingProbe());
        Assert.False(selected.Passed);
        Assert.Null(selected.WithinAttemptRerun);
        cache.RecordExecution(_partition, selected);
        Assert.NotNull(cache.CompleteAttempt());

        var journal = GoalOperationJournal.ReadStrict(_root, GoalId);
        var observations = journal.Entries.Where(entry => entry.Operation is
            "acceptance:partition-within-attempt-retry" or "acceptance:partition-within-attempt-probe").ToArray();
        Assert.Equal(2, observations.Length);
        foreach (var observation in observations)
        {
            Assert.Equal(GoalOperationStatus.Completed, observation.Status);
            Assert.Equal(GoalId, observation.GoalId);
            Assert.Equal(Candidate, observation.BranchHeadSha);
            Assert.Equal(Main, observation.MainHeadSha);
            Assert.Equal(Attempt, observation.PartitionAttemptId);
            Assert.Null(observation.AcceptanceOutcome);
            var receipt = Assert.IsType<Dictionary<string, JsonElement>>(observation.AuditProperties)["partitionRetryReceipt"];
            Assert.Equal(2, receipt.GetProperty("exitCode").GetInt32());
            Assert.Equal(19064, receipt.GetProperty("discoveredTestCount").GetInt32());
            Assert.Equal(19061, receipt.GetProperty("executedTestCount").GetInt32());
            Assert.Equal(3, receipt.GetProperty("notExecutedTestCount").GetInt32());
            Assert.Equal("Failed", receipt.GetProperty("trxOutcome").GetString());
            Assert.Equal("original-failure.err", receipt.GetProperty("diagnosticPath").GetString());
            Assert.Equal(new string('a', 64), receipt.GetProperty("diagnosticSha256").GetString());
            Assert.Equal(Attempt + ":remainder:0", receipt.GetProperty("originalInvocationId").GetString());
            Assert.Equal(Attempt + ":remainder:1", receipt.GetProperty("retryInvocationId").GetString());
            AssertAllPropertiesPreserved(FindSource(observation.IdempotencyKey), observation);
        }
        var retry = Assert.Single(observations, entry => entry.Operation.EndsWith("retry", StringComparison.Ordinal));
        var probe = Assert.Single(observations, entry => entry.Operation.EndsWith("probe", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, retry.AuditProperties!["partitionRetryReceipt"].GetProperty("flakeConfirmed").ValueKind);
        Assert.True(probe.AuditProperties!["partitionRetryReceipt"].GetProperty("flakeConfirmed").GetBoolean());
        Assert.Equal("RED", probe.PartitionVerdict);
        Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(journal));
        Assert.False(GoalOperationJournal.HasCompletedRecordEvidence(journal));
        Assert.Null(CreateCache("next-attempt").TryReuse(_partition));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalRecordedObservationShape_PreservesEveryAuditPropertyAndOriginalBytes(bool compressed)
    {
        // Representative historical shape, with portable diagnostic paths. Exact original rows and
        // SHA256 provenance are supplied separately in the Operator's packaged timeline for Acceptance.
        var rows = RepresentativeHistoricalRows();
        VerifyDisposableHistoricalRows(rows, compressed);
    }

    // Acceptance can pass its hash-verified exact rows explicitly to these same assertions, without
    // embedding machine-local audit paths in source or depending on ambient production state.
    public static void VerifyDisposableHistoricalRows(string[] rows, bool compressed)
    {
        using var fixture = new AcceptancePartitionJournalContractTests();
        fixture.AssertHistoricalRows(rows, compressed);
    }

    private void AssertHistoricalRows(string[] rows, bool compressed)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", rows) + "\n");
        var path = GoalOperationJournal.PathFor(_root, GoalId) + (compressed ? ".gz" : "");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (compressed)
        {
            using var file = File.Create(path);
            using var gzip = new GZipStream(file, CompressionMode.Compress);
            gzip.Write(bytes);
        }
        else File.WriteAllBytes(path, bytes);
        var before = SHA256.HashData(File.ReadAllBytes(path));

        var journal = GoalOperationJournal.ReadStrict(_root, GoalId);

        Assert.Equal(2, journal.Entries.Count);
        for (var index = 0; index < rows.Length; index++)
        {
            var entry = journal.Entries[index];
            Assert.Equal(GoalOperationStatus.Completed, entry.Status);
            AssertAllPropertiesPreserved(JsonNode.Parse(rows[index])!.AsObject(), entry);
        }
        Assert.Equal("RED", journal.Entries[1].PartitionVerdict);
        Assert.True(journal.Entries[1].AuditProperties!["partitionRetryReceipt"].GetProperty("flakeConfirmed").GetBoolean());
        Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(journal));
        Assert.False(GoalOperationJournal.HasCompletedRecordEvidence(journal));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData("idempotencyKey")]
    [InlineData("goalId")]
    [InlineData("operation")]
    [InlineData("status")]
    [InlineData("at")]
    [InlineData("branchHeadSha")]
    [InlineData("mainHeadSha")]
    [InlineData("partitionVerdictCacheKey")]
    [InlineData("partitionPairKey")]
    [InlineData("partitionId")]
    [InlineData("partitionFilterHash")]
    [InlineData("partitionAttemptId")]
    [InlineData("partitionRetryReceipt")]
    public void LegacyObservation_MissingRequiredField_FailsClosedWithPhysicalLineAndPath(string field)
    {
        var row = JsonNode.Parse(RepresentativeHistoricalRows()[0])!.AsObject();
        row.Remove(field);
        WriteRows(ValidFailedAcceptance(), row.ToJsonString());

        var error = Assert.Throws<FormatException>(() => GoalOperationJournal.ReadStrict(_root, GoalId));

        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("missing-field", error.Message, StringComparison.Ordinal);
        Assert.Contains("$." + field, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("original-failure.err", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown-status")]
    [InlineData("numeric-status")]
    [InlineData("numeric-string-status")]
    [InlineData("combined-status")]
    [InlineData("recorded-acceptance")]
    [InlineData("recorded-landing")]
    [InlineData("recorded-unrelated")]
    [InlineData("empty-goal")]
    [InlineData("invalid-time")]
    [InlineData("missing-receipt-field")]
    [InlineData("wrong-receipt-type")]
    [InlineData("wrong-extra-receipt-type")]
    [InlineData("wrong-flake-type")]
    [InlineData("candidate-mismatch")]
    [InlineData("partition-mismatch")]
    [InlineData("invocation-mismatch")]
    [InlineData("key-mismatch")]
    [InlineData("acceptance-promotion")]
    [InlineData("missing-probe-verdict")]
    [InlineData("missing-probe-result")]
    [InlineData("duplicate-status")]
    [InlineData("truncated")]
    public void UnsupportedOrMalformedObservation_FailsClosed(string mutation)
    {
        var row = JsonNode.Parse(RepresentativeHistoricalRows()[mutation.StartsWith("missing-probe", StringComparison.Ordinal) ? 1 : 0])!.AsObject();
        var receipt = row["partitionRetryReceipt"]!.AsObject();
        switch (mutation)
        {
            case "unknown-status": row["status"] = "unrecognized"; break;
            case "numeric-status": row["status"] = 99; break;
            case "numeric-string-status": row["status"] = "1"; break;
            case "combined-status": row["status"] = "Begin, Completed"; break;
            case "recorded-acceptance": row["operation"] = "acceptance"; row["partitionVerdict"] = "RED"; break;
            case "recorded-landing": row["operation"] = "conductor:land"; break;
            case "recorded-unrelated": row["operation"] = "another-observation"; break;
            case "empty-goal": row["goalId"]!["value"] = " "; break;
            case "invalid-time": row["at"] = "not-a-time"; break;
            case "missing-receipt-field": receipt.Remove("diagnosticSha256"); break;
            case "wrong-receipt-type": receipt["exitCode"] = "two"; break;
            case "wrong-extra-receipt-type": receipt["notExecutedTestCount"] = "three"; break;
            case "wrong-flake-type": receipt["flakeConfirmed"] = "true"; break;
            case "candidate-mismatch": row["branchHeadSha"] = "other-candidate"; break;
            case "partition-mismatch": receipt["partitionId"] = "other-partition"; break;
            case "invocation-mismatch": receipt["retryInvocationId"] = "other-attempt:1"; break;
            case "key-mismatch": row["idempotencyKey"] = "unbound-key"; break;
            case "acceptance-promotion": row["acceptanceOutcome"] = "passed"; break;
            case "missing-probe-verdict": row.Remove("partitionVerdict"); break;
            case "missing-probe-result": receipt.Remove("flakeConfirmed"); break;
        }
        var line = row.ToJsonString();
        if (mutation == "truncated") line = line[..^1];
        if (mutation == "duplicate-status") line = line.Insert(1, "\"status\":\"Completed\",");
        WriteRows(line);

        var error = Assert.Throws<FormatException>(() => GoalOperationJournal.ReadStrict(_root, GoalId));
        Assert.Contains("line 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("path=$", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 1024);
    }

    [Fact]
    public void FailedRedAcceptanceAndCompletedObservations_DoNotSupplySuccessfulAcceptanceEvidence()
    {
        WriteRows([ValidFailedAcceptance(), .. RepresentativeHistoricalRows()]);

        var journal = GoalOperationJournal.ReadStrict(_root, GoalId);

        Assert.Equal(GoalOperationStatus.Failed, journal.Entries.Single(entry => entry.Operation == "acceptance").Status);
        Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(journal));
        Assert.False(GoalOperationJournal.HasCompletedRecordEvidence(journal));
    }

    private string[] RepresentativeHistoricalRows()
    {
        var cache = CreateCache(Attempt);
        cache.RecordWithinAttemptRetry(_partition, FirstRun(), Attempt + ":remainder:0", Attempt + ":remainder:1",
            new AcceptanceRetainedDiagnostic("original-failure.err", new string('a', 64)));
        cache.SelectPartitionVerdict(_partition, FirstRun(), PassingProbe());
        var rows = File.ReadAllLines(cache.JournalPath).TakeLast(2).Select(line =>
        {
            var row = JsonNode.Parse(line)!.AsObject();
            row["status"] = "recorded";
            row["partitionClosureHash"] = "original-closure";
            row["additionalAuditFacts"] = JsonNode.Parse("{\"receipts\":[\"failed-first-run\",\"passing-probe\"],\"retained\":true}");
            return row.ToJsonString();
        }).ToArray();
        File.Delete(cache.JournalPath);
        return rows;
    }

    private AcceptancePartitionVerdictCache CreateCache(string attempt) =>
        Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(GoalId, _root, [_partition], 5, true,
                _ => Candidate, _ => Main, _ => Candidate, () => attempt, () => "manifest", () => false)));

    private AcceptanceCheckResult FirstRun() => new(_partition.Name, false, 2, "original hard RED",
        CompletionDecision: new AcceptanceShardCompletionDecision(false,
            AcceptanceShardCompletionPredicates.AssemblyCleanupFailure, false, 2, 19064, 19061, "Failed",
            NotExecutedTestCount: 3));

    private AcceptanceCheckResult PassingProbe() => new(_partition.Name, true, 0, null,
        TestResultPaths: ["probe.trx"], ExecutedTestCount: 19064,
        CompletionDecision: new AcceptanceShardCompletionDecision(true, null, false, 0, 19064, 19064, "Passed"));

    private JsonObject FindSource(string key) => File.ReadAllLines(GoalOperationJournal.PathFor(_root, GoalId))
        .Select(line => JsonNode.Parse(line)!.AsObject()).Single(row => row["idempotencyKey"]!.GetValue<string>() == key);

    private static void AssertAllPropertiesPreserved(JsonObject source, GoalOperationJournalEntry entry)
    {
        var restored = JsonSerializer.SerializeToNode(entry, JsonOptions)!.AsObject();
        foreach (var property in source)
        {
            if (property.Key == "status") continue; // Historical operational alias is normalized in memory.
            Assert.True(restored.ContainsKey(property.Key), "Missing audit property: " + property.Key);
            Assert.True(JsonNode.DeepEquals(property.Value, restored[property.Key]), "Changed audit property: " + property.Key);
        }
    }

    private static string ValidFailedAcceptance() => JsonSerializer.Serialize(new GoalOperationJournalEntry(
        "failed-acceptance", GoalId, "acceptance", GoalOperationStatus.Failed,
        DateTimeOffset.UnixEpoch, "RED acceptance", Candidate, Main, "failed"), JsonOptions);

    private void WriteRows(params string[] rows)
    {
        var path = GoalOperationJournal.PathFor(_root, GoalId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, rows);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
