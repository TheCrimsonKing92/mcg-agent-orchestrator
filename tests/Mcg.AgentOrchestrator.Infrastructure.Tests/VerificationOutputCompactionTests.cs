using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test owns its kernel and uses fixed verification timestamps.
public sealed class VerificationOutputCompactionTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoRounds_ExportRestoreExport_PreservesTextWithCompactFields()
    {
        var firstOutput = TextWithMarker("round-one-output", 2_000);
        var secondOutput = TextWithMarker("round-two-output", 2_000);
        var firstError = TextWithMarker("round-one-error", 300);
        var secondError = TextWithMarker("round-two-error", 300);
        var snapshot = Export(
            Verification(firstOutput, firstError, CompletedAt),
            Verification(secondOutput, secondError, CompletedAt.AddMinutes(1)));
        var task = SnapshotTask(snapshot);

        Assert.Equal(2, task.VerificationHistory!.Count);
        AssertCompacted(task.LastVerification!);
        Assert.All(task.VerificationHistory, AssertCompacted);

        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(snapshot);
        var restored = Assert.Single(Assert.Single(restoredKernel.Goals).Tasks);
        AssertRestored(restored.LastVerification!, secondOutput, secondError);
        Assert.Collection(restored.VerificationHistory,
            record => AssertRestored(record, firstOutput, firstError),
            record => AssertRestored(record, secondOutput, secondError));

        var exportedAgain = SnapshotTask(restoredKernel.ExportSnapshot());
        Assert.Equal(TextFields(task.LastVerification!), TextFields(exportedAgain.LastVerification!));
        Assert.Equal(task.VerificationHistory.Select(TextFields).ToArray(),
            exportedAgain.VerificationHistory!.Select(TextFields).ToArray());
    }

    [Fact]
    public void LongOutput_RoundTrip_PreservesFullTextBeyondPreview()
    {
        var output = TextWithMarker("long-middle-marker", VerificationTextBounds.BoundThreshold + 5_000);
        var snapshot = Export(Verification(output, string.Empty, CompletedAt));
        var task = SnapshotTask(snapshot);

        Assert.Equal(VerificationTextBounds.BoundThreshold + 5_000, output.Length);
        Assert.StartsWith("head-marker\n", output);
        Assert.EndsWith("\ntail-marker", output);
        Assert.DoesNotContain("long-middle-marker", task.LastVerification!.StandardOutput);
        foreach (var stored in task.VerificationHistory!.Prepend(task.LastVerification))
        {
            Assert.Equal(output, stored.AuthoritativeStandardOutput);
            Assert.Null(stored.AuthoritativeStandardOutputUnavailableReason);
        }

        var restored = Restore(snapshot);
        Assert.Equal(output, restored.LastVerification!.AuthoritativeStandardOutput);
        Assert.Contains("long-middle-marker", restored.LastVerification.AuthoritativeStandardOutput!);
        Assert.Equal(output, Assert.Single(restored.VerificationHistory).AuthoritativeStandardOutput);
    }

    [Fact]
    public void MissingFullOutput_RoundTrip_PreservesUnavailableReason()
    {
        var record = new TaskVerificationRecord("verify", "C:\\repo", 0, "preview", string.Empty,
            CompletedAt, FullStandardOutputUnavailableReason: "missing");
        var snapshot = Export(record);
        var task = SnapshotTask(snapshot);
        foreach (var stored in task.VerificationHistory!.Prepend(task.LastVerification!))
        {
            Assert.Null(stored.AuthoritativeStandardOutput);
            Assert.Equal("missing", stored.AuthoritativeStandardOutputUnavailableReason);
        }

        var restored = Restore(snapshot);
        foreach (var verification in restored.VerificationHistory.Prepend(restored.LastVerification!))
        {
            Assert.Null(verification.AuthoritativeStandardOutput);
            Assert.Equal("missing", verification.FullStandardOutputUnavailableReason);
            Assert.False(verification.StandardOutputIsAuthoritative);
        }
    }

    [Fact]
    public void EqualTextWithReason_Export_KeepsTextAndReason()
    {
        var record = new TaskVerificationRecord("verify", "C:\\repo", 0, "kept-output", string.Empty,
            CompletedAt, FullStandardOutput: "kept-output", FullStandardOutputUnavailableReason: "kept-reason");
        var snapshot = Export(record);
        var task = SnapshotTask(snapshot);
        foreach (var stored in task.VerificationHistory!.Prepend(task.LastVerification!))
        {
            Assert.Equal("kept-output", stored.AuthoritativeStandardOutput);
            Assert.Equal("kept-reason", stored.AuthoritativeStandardOutputUnavailableReason);
        }

        var restored = Restore(snapshot);
        Assert.Equal("kept-output", restored.LastVerification!.AuthoritativeStandardOutput);
        Assert.Equal("kept-output", Assert.Single(restored.VerificationHistory).AuthoritativeStandardOutput);
    }

    [Fact]
    public void LegacyBothNull_Restore_DoesNotPromotePreview()
    {
        var snapshot = Export(Verification("legacy-preview", string.Empty, CompletedAt));
        var legacy = SnapshotTask(snapshot).LastVerification! with
        {
            AuthoritativeStandardOutput = null,
            AuthoritativeStandardOutputUnavailableReason = null
        };
        var restored = Restore(WithVerification(snapshot, legacy));

        foreach (var record in restored.VerificationHistory.Prepend(restored.LastVerification!))
        {
            Assert.Equal("legacy-preview", record.StandardOutput);
            Assert.Null(record.AuthoritativeStandardOutput);
            Assert.Equal("legacy-snapshot-authoritative-output-unavailable", record.FullStandardOutputUnavailableReason);
            Assert.False(record.StandardOutputIsAuthoritative);
        }
    }

    [Fact]
    public void LegacyDuplicate_Restore_SharesPreviewInstance()
    {
        var preview = TextWithMarker("legacy-duplicate", 2_000);
        var duplicate = new string(preview.ToCharArray());
        var legacy = new TaskVerificationSnapshot("verify", "C:\\repo", 0, preview, string.Empty,
            CompletedAt, AuthoritativeStandardOutput: duplicate, AuthoritativeStandardError: string.Empty);
        Assert.Equal(legacy.StandardOutput, legacy.AuthoritativeStandardOutput);
        Assert.NotSame(legacy.StandardOutput, legacy.AuthoritativeStandardOutput);
        var snapshot = Export(Verification(preview, string.Empty, CompletedAt));

        var restored = Restore(WithVerification(snapshot, legacy));

        AssertRestored(restored.LastVerification!, preview, string.Empty);
        AssertRestored(Assert.Single(restored.VerificationHistory), preview, string.Empty);
    }

    private static TaskVerificationRecord Verification(string output, string error, DateTimeOffset at) =>
        new("verify", "C:\\repo", 0, output, error, at, FullStandardOutput: output, FullStandardError: error);

    private static OrchestratorSnapshot Export(params TaskVerificationRecord[] records)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        var goal = kernel.CreateGoal("Preserve verification text", [task]);
        foreach (var record in records)
            kernel.RecordTaskVerification(goal.Id, task.Id, record);
        return kernel.ExportSnapshot();
    }

    private static TaskSnapshot SnapshotTask(OrchestratorSnapshot snapshot) =>
        Assert.Single(Assert.Single(snapshot.Goals).Tasks);

    private static TaskSpec Restore(OrchestratorSnapshot snapshot) =>
        Assert.Single(Assert.Single(AgentOrchestratorKernel.FromSnapshot(snapshot).Goals).Tasks);

    private static OrchestratorSnapshot WithVerification(
        OrchestratorSnapshot snapshot, TaskVerificationSnapshot verification)
    {
        var goal = Assert.Single(snapshot.Goals);
        return snapshot with
        {
            Goals = [goal with
            {
                Tasks = [SnapshotTask(snapshot) with
                {
                    LastVerification = verification,
                    VerificationHistory = [verification]
                }]
            }]
        };
    }

    private static void AssertCompacted(TaskVerificationSnapshot record)
    {
        Assert.Null(record.AuthoritativeStandardOutput);
        Assert.Null(record.AuthoritativeStandardError);
        Assert.Equal(VerificationSnapshotText.StoredAsBoundedText, record.AuthoritativeStandardOutputUnavailableReason);
        Assert.Equal(VerificationSnapshotText.StoredAsBoundedText, record.AuthoritativeStandardErrorUnavailableReason);
    }

    private static void AssertRestored(TaskVerificationRecord record, string output, string error)
    {
        Assert.Equal(output, record.AuthoritativeStandardOutput);
        Assert.Equal(error, record.AuthoritativeStandardError);
        Assert.Null(record.FullStandardOutputUnavailableReason);
        Assert.Null(record.FullStandardErrorUnavailableReason);
        Assert.True(record.StandardOutputIsAuthoritative);
        Assert.True(record.StandardErrorIsAuthoritative);
        Assert.Same(record.StandardOutput, record.AuthoritativeStandardOutput);
        Assert.Same(record.StandardError, record.AuthoritativeStandardError);
    }

    private static (string, string, string?, string?, string?, string?) TextFields(TaskVerificationSnapshot record) =>
        (record.StandardOutput, record.StandardError, record.AuthoritativeStandardOutput,
            record.AuthoritativeStandardError, record.AuthoritativeStandardOutputUnavailableReason,
            record.AuthoritativeStandardErrorUnavailableReason);

    private static string TextWithMarker(string marker, int length)
    {
        var head = "head-marker\n" + new string('a', length / 2 - "head-marker\n".Length);
        var middle = "\n" + marker + "\n";
        const string tail = "\ntail-marker";
        return head + middle + new string('b', length - head.Length - middle.Length - tail.Length) + tail;
    }
}
