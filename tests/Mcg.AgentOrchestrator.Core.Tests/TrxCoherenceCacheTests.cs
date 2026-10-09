using System.Security.Cryptography;
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

    [Fact(DisplayName = "A file changed during evaluation cannot seed a passing memo")]
    public void ChangeDuringEvaluationFailsAndIsNotCached()
    {
        var path = Path.Combine(directory, "during.trx");
        File.WriteAllText(path, Trx(total: 1));
        var evaluated = false;

        Assert.False(TrxCoherenceCache.Evaluate(path, fullPath =>
        {
            evaluated = true;
            File.WriteAllText(fullPath, Trx(total: 20));
            return true;
        }));
        Assert.True(evaluated);
        Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(2, TrxCoherenceCache.LoadCount);
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
