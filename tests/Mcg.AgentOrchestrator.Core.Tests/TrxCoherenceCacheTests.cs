using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core.Tests;

// Each fixture owns a unique directory. No other Core.Tests class loads TRX coherence evidence.
public sealed class TrxCoherenceCacheTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "trx-coherence-" + Guid.NewGuid().ToString("N"));

    public TrxCoherenceCacheTests()
    {
        Directory.CreateDirectory(directory);
        TrxCoherenceCache.Reset();
    }

    [Fact(DisplayName = "Repeated landing reads parse each unchanged TRX once")]
    public void RepeatedLandingReadsLoadEachFileOnce()
    {
        var receipt = CreateReceipt();

        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        Assert.Equal(2, TrxCoherenceCache.LoadCount);
    }

    [Fact(DisplayName = "Changed stamps re-evaluate incoherent TRX evidence")]
    public void ChangedFileIsReevaluated()
    {
        var receipt = CreateReceipt();
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        var path = receipt.GateTestResultPaths[1];
        var previousStamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, Trx(total: 2));
        File.SetLastWriteTimeUtc(path, previousStamp.AddMinutes(1));

        Assert.False(receipt.HasAuthoritativeLandingEvidence);
        // Hash mismatch alone could reject the receipt; this proves coherence invalidation too.
        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(receipt.GateTestResultPaths));
        Assert.Equal(3, TrxCoherenceCache.LoadCount);
    }

    [Fact(DisplayName = "Removed evidence never reuses a cached passing verdict")]
    public void RemovedFileFailsWithoutAnotherParse()
    {
        var receipt = CreateReceipt();
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        File.Delete(receipt.GateTestResultPaths[1]);

        Assert.False(receipt.HasAuthoritativeLandingEvidence);
        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(receipt.GateTestResultPaths));
        Assert.Equal(2, TrxCoherenceCache.LoadCount);
    }

    [Fact(DisplayName = "Unreadable cached evidence fails and recovers after unlocking")]
    public void LockedFileDoesNotReuseCachedTrue()
    {
        var receipt = CreateReceipt();
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        using (var locked = new FileStream(receipt.GateTestResultPaths[1], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(receipt.GateTestResultPaths));
        }
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        Assert.Equal(2, TrxCoherenceCache.LoadCount);
    }

    [Theory(DisplayName = "Malformed and unexecuted evidence keeps its failing verdict")]
    [InlineData("<TestRun")]
    [InlineData("<TestRun />")]
    [InlineData("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results /><ResultSummary><Counters total=\"0\" executed=\"0\" passed=\"0\" failed=\"0\" /></ResultSummary></TestRun>")]
    public void InvalidEvidenceRemainsFalse(string content)
    {
        var path = Path.Combine(directory, "invalid.trx");
        File.WriteAllText(path, content);

        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(1, TrxCoherenceCache.LoadCount);
    }

    [Fact(DisplayName = "Timestamp changes invalidate same-size coherence verdicts")]
    public void TimestampChangeReevaluatesSameSizeFile()
    {
        var path = Path.Combine(directory, "stamp.trx");
        File.WriteAllText(path, Trx(total: 1));
        var stamp = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        File.WriteAllText(path, Trx(total: 2));
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(1));
        Assert.Equal(length, new FileInfo(path).Length);

        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(2, TrxCoherenceCache.LoadCount);

        File.WriteAllText(path, Trx(total: 1));
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(2));
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(3, TrxCoherenceCache.LoadCount);
    }

    [Fact(DisplayName = "Length changes invalidate verdicts when timestamps match")]
    public void LengthChangeReevaluatesSameTimestampFile()
    {
        var path = Path.Combine(directory, "length.trx");
        File.WriteAllText(path, Trx(total: 1));
        var stamp = File.GetLastWriteTimeUtc(path);
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        File.WriteAllText(path, Trx(total: 20));
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(2, TrxCoherenceCache.LoadCount);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void SameStampRewrite_ReevaluatesChangedContent(int original, int replacement)
    {
        var path = Path.Combine(directory, "same-stamp.trx");
        File.WriteAllText(path, Trx(total: original));
        var stamp = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        Assert.Equal(original == 1, AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));

        File.WriteAllText(path, Trx(total: replacement));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));

        Assert.Equal(replacement == 1, AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(2, TrxCoherenceCache.LoadCount);
        Assert.Equal(replacement == 1, AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(2, TrxCoherenceCache.LoadCount);
    }

    [Fact]
    public void SameStampRewrite_LandingRejectsFreshlyBoundIncoherentEvidence()
    {
        var receipt = CreateReceipt();
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        var path = receipt.GateTestResultPaths[1];
        var stamp = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;

        File.WriteAllText(path, Trx(total: 2));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        receipt = receipt with
        {
            GateEvidenceArtifacts = receipt.GateEvidenceArtifacts
                .Select(artifact => artifact.Path == path ? Artifact("trx", path) : artifact)
                .ToArray()
        };

        // Refreshing the artifact hash makes coherence the discriminating landing check.
        Assert.False(receipt.HasAuthoritativeLandingEvidence);
        Assert.Equal(3, TrxCoherenceCache.LoadCount);
    }

    [Fact]
    public void Evaluation_ActiveReadHandlePreventsContentChange()
    {
        var path = Path.Combine(directory, "during.trx");
        File.WriteAllText(path, Trx(total: 1));
        var evaluated = false;

        Assert.True(TrxCoherenceCache.Evaluate(path, fullPath =>
        {
            evaluated = true;
            Assert.Throws<IOException>(() => File.WriteAllText(fullPath, Trx(total: 20)));
            Assert.Throws<IOException>(() => File.Delete(fullPath));
            return true;
        }));
        Assert.True(evaluated);
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(1, TrxCoherenceCache.LoadCount);
    }

    [Fact(DisplayName = "Overflow evicts old stamps instead of growing the memo forever")]
    public void EntryCeilingEvictsOldStamps()
    {
        var path = Path.Combine(directory, "bounded.trx");
        File.WriteAllText(path, Trx(total: 1));
        var firstStamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index <= 2048; index++)
        {
            File.SetLastWriteTimeUtc(path, firstStamp.AddMinutes(index));
            Assert.True(TrxCoherenceCache.Evaluate(path, static _ => true));
        }
        File.SetLastWriteTimeUtc(path, firstStamp);
        var loadsBeforeProbe = TrxCoherenceCache.LoadCount;

        Assert.False(TrxCoherenceCache.Evaluate(path, static _ => false));
        Assert.Equal(loadsBeforeProbe + 1, TrxCoherenceCache.LoadCount);
    }

    [Fact]
    public void Store_RestartReusesPassingAndFailingVerdictsWithoutParsing()
    {
        var passing = Path.Combine(directory, "passing.trx");
        var failing = Path.Combine(directory, "failing.trx");
        File.WriteAllText(passing, Trx(total: 1));
        File.WriteAllText(failing, "<TestRun");
        TrxCoherenceVerdictStore.Load(directory);
        TrxCoherenceVerdictStore.EnableAppend(directory);

        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([passing]));
        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([failing]));
        Assert.Equal(2, TrxCoherenceCache.LoadCount);
        var originalLines = File.ReadAllLines(StorePath);
        Assert.Equal(2, originalLines.Length);

        TrxCoherenceCache.Reset();
        TrxCoherenceVerdictStore.Load(directory);
        TrxCoherenceVerdictStore.EnableAppend(directory);

        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([passing]));
        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([failing]));
        Assert.Equal(0, TrxCoherenceCache.LoadCount);
        Assert.Equal(originalLines, File.ReadAllLines(StorePath));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(20, true)]
    public void Store_RestartReevaluatesChangedContent(int total, bool restoreStamp)
    {
        var path = Path.Combine(directory, "changed.trx");
        File.WriteAllText(path, Trx(total: 1));
        var stamp = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        TrxCoherenceVerdictStore.EnableAppend(directory);
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        TrxCoherenceCache.Reset();
        TrxCoherenceVerdictStore.Load(directory);

        File.WriteAllText(path, Trx(total));
        File.SetLastWriteTimeUtc(path, restoreStamp ? stamp : stamp.AddMinutes(1));
        Assert.Equal(total == 2, length == new FileInfo(path).Length);
        Assert.Equal(restoreStamp, stamp == File.GetLastWriteTimeUtc(path));

        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(1, TrxCoherenceCache.LoadCount);
    }

    [Fact]
    public void Store_RestartRejectsRemovedFileWithoutParsing()
    {
        var path = Path.Combine(directory, "removed.trx");
        File.WriteAllText(path, Trx(total: 1));
        TrxCoherenceVerdictStore.EnableAppend(directory);
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        TrxCoherenceCache.Reset();
        TrxCoherenceVerdictStore.Load(directory);
        File.Delete(path);

        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(0, TrxCoherenceCache.LoadCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("unknown-version")]
    [InlineData("locked")]
    public void Store_UnusablePersistenceLeavesEvaluationCold(string scenario)
    {
        var path = Path.Combine(directory, "cold.trx");
        File.WriteAllText(path, Trx(total: 1));
        if (scenario != "missing")
        {
            File.WriteAllText(StorePath, scenario switch
            {
                "empty" => "",
                "malformed" => "{torn",
                "unknown-version" => StoreLine(path, verdict: false, version: 99),
                _ => StoreLine(path, verdict: false)
            });
        }

        using (var locked = scenario == "locked"
            ? new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            : null)
        {
            TrxCoherenceVerdictStore.Load(directory);
        }

        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(1, TrxCoherenceCache.LoadCount);
    }

    [Fact]
    public void Store_MalformedLineDoesNotHideFollowingValidVerdict()
    {
        var path = Path.Combine(directory, "mixed.trx");
        File.WriteAllText(path, Trx(total: 1));
        File.WriteAllLines(StorePath, ["{torn", StoreLine(path, verdict: true)]);

        TrxCoherenceVerdictStore.Load(directory);

        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(0, TrxCoherenceCache.LoadCount);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("path")]
    [InlineData("length")]
    [InlineData("ticks")]
    [InlineData("sha256")]
    [InlineData("verdict")]
    public void Store_IncompleteRecordIsSkipped(string missingField)
    {
        var path = Path.Combine(directory, "incomplete.trx");
        File.WriteAllText(path, Trx(total: 1));
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(StoreLine(path, verdict: false))!;
        fields.Remove(missingField);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(fields));

        TrxCoherenceVerdictStore.Load(directory);

        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(1, TrxCoherenceCache.LoadCount);
    }

    [Fact]
    public void Store_FailingAppendPreservesComputedAndCachedVerdict()
    {
        var path = Path.Combine(directory, "append.trx");
        File.WriteAllText(path, Trx(total: 1));
        // An existing regular file cannot be the store directory.
        TrxCoherenceVerdictStore.EnableAppend(path);

        Assert.True(TrxCoherenceCache.Evaluate(path, static _ => true));
        Assert.True(TrxCoherenceCache.Evaluate(path, static _ => throw new InvalidOperationException("cached verdict expected")));
        Assert.Equal(1, TrxCoherenceCache.LoadCount);
    }

    [Fact]
    public void Store_LoadIsOncePerProcessAndResetDetachesAppend()
    {
        var path = Path.Combine(directory, "once.trx");
        File.WriteAllText(path, Trx(total: 1));
        File.WriteAllText(StorePath, StoreLine(path, verdict: true));
        TrxCoherenceVerdictStore.Load(directory);
        TrxCoherenceVerdictStore.EnableAppend(directory);
        File.WriteAllText(StorePath, StoreLine(path, verdict: false));

        TrxCoherenceVerdictStore.Load(directory);
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(0, TrxCoherenceCache.LoadCount);

        TrxCoherenceCache.Reset();
        TrxCoherenceVerdictStore.Load(directory);
        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(0, TrxCoherenceCache.LoadCount);
        var stored = File.ReadAllText(StorePath);
        var freshPath = Path.Combine(directory, "after-reset.trx");
        File.WriteAllText(freshPath, Trx(total: 1));
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([freshPath]));
        Assert.Equal(stored, File.ReadAllText(StorePath));
    }

    [Fact]
    public void Store_SeedingStopsAtCeilingAndDuplicateKeysUseLastVerdict()
    {
        var path = Path.Combine(directory, "ceiling.trx");
        File.WriteAllText(path, Trx(total: 1));
        var stamp = File.GetLastWriteTimeUtc(path);
        var lines = Enumerable.Range(0, 2049)
            .Select(index => StoreLine(path, verdict: true, ticks: stamp.AddMinutes(index).Ticks))
            .Append(StoreLine(path, verdict: false));
        File.WriteAllLines(StorePath, lines);

        TrxCoherenceVerdictStore.Load(directory);

        Assert.False(TrxCoherenceCache.Evaluate(path, static _ => throw new InvalidOperationException("seed expected")));
        Assert.Equal(0, TrxCoherenceCache.LoadCount);
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(2047));
        Assert.True(TrxCoherenceCache.Evaluate(path, static _ => throw new InvalidOperationException("seed expected")));
        Assert.Equal(0, TrxCoherenceCache.LoadCount);
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(2048));
        Assert.False(TrxCoherenceCache.Evaluate(path, static _ => false));
        Assert.Equal(1, TrxCoherenceCache.LoadCount);
    }

    private string StorePath => Path.Combine(directory, "trx-coherence-verdicts.jsonl");

    private static string StoreLine(string path, bool verdict, int version = 1, long? ticks = null) =>
        JsonSerializer.Serialize(new
        {
            version,
            path = Path.GetFullPath(path),
            length = new FileInfo(path).Length,
            ticks = ticks ?? File.GetLastWriteTimeUtc(path).Ticks,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            verdict
        });

    private MergeTrainReceipt CreateReceipt()
    {
        var paths = new[] { Path.Combine(directory, "a.trx"), Path.Combine(directory, "b.trx") };
        foreach (var path in paths)
        {
            File.WriteAllText(path, Trx(total: 1));
        }
        var verdictPath = Path.Combine(directory, "gate-verdict.txt");
        File.WriteAllText(verdictPath, "passed");
        var identity = MergeTrainIdentity.Create(
            [Binding('1', 'a'), Binding('2', 'b')], new string('c', 40), new string('d', 40), "manifest-v1");
        return new MergeTrainReceipt("receipt", identity, MergeTrainGateOutcome.Passed,
            DateTimeOffset.UnixEpoch, 1, [], 0, paths, ValidForLanding: true)
        {
            GateEvidenceArtifacts = [Artifact("trx", paths[0]), Artifact("trx", paths[1]), Artifact("gate-verdict", verdictPath)]
        };
    }

    private static MergeTrainMemberBinding Binding(char goal, char revision) => new(
        new GoalId(new string(goal, 32)), new string(revision, 40), new string(revision, 40),
        ["src/Example.cs"], [], ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
        "Clean", "NoConflictsDetected", new string(revision, 40));

    private static AcceptanceCohortEvidenceArtifact Artifact(string kind, string path) => new(
        kind, Path.GetFullPath(path), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), new FileInfo(path).Length);

    private static string Trx(int total) => $$"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results><UnitTestResult outcome="Passed" /></Results>
          <ResultSummary><Counters total="{{total}}" executed="1" passed="1" failed="0" /></ResultSummary>
        </TestRun>
        """;

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        TrxCoherenceCache.Reset();
    }
}
