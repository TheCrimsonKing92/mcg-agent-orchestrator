using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BuildSlotHoldRecordTests
{
    [Fact]
    public void Witness_OnlyDirectNamedAssembliesWithinWindow_AreIncluded()
    {
        var root = CreateTempDirectory();
        try
        {
            var since = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
            var written = since.AddMinutes(1).UtcDateTime;
            foreach (var (relative, time) in new[]
            {
                ("A/debug/A.dll", written), ("C/debug/ref/C.dll", written),
                ("A/debug/other.dll", written), ("B/debug/B.dll", since.AddSeconds(-1).UtcDateTime)
            })
            {
                var file = Path.Combine(root, "obj", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "assembly");
                File.SetLastWriteTimeUtc(file, time);
            }
            Assert.Equal([$"A/debug@{written.Ticks}"], BuildSlotHoldRecord.WitnessCompiledProjects(root, since, since.AddHours(1)));
            Assert.Equal([$"A/debug@{written.Ticks}"], BuildSlotHoldRecord.WitnessCompiledProjects(root, written, written));
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(Path.Combine(empty, "obj"));
            Assert.Empty(BuildSlotHoldRecord.WitnessCompiledProjects(empty, since, since.AddDays(1))!);
            Assert.Null(BuildSlotHoldRecord.WitnessCompiledProjects(Path.Combine(root, "missing"), since, since.AddDays(1)));
            Assert.Null(BuildSlotHoldRecord.WitnessCompiledProjects(null, since, since.AddDays(1)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Details_LabelTimeAndCompileStates_HaveExactGrammar()
    {
        var at = new DateTimeOffset(2026, 10, 16, 1, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal("holder=pre-review:_a_b acquiredAt=2026-10-16T00:00:00.0000000Z heldMs=1234 compiled=A/debug@1|B/Release@2",
            new BuildSlotHoldRecord("pre-review: a\tb", at, at.AddMilliseconds(1234.9), ["A/debug@1", "B/Release@2"]).FormatDetails());
        Assert.Equal("holder=unlabeled acquiredAt=2026-10-16T00:00:00.0000000Z heldMs=0 compiled=none",
            new BuildSlotHoldRecord(null, at, at.AddSeconds(-1), []).FormatDetails());
        Assert.Equal("holder=unlabeled acquiredAt=2026-10-16T00:00:00.0000000Z heldMs=0 compiled=unknown",
            new BuildSlotHoldRecord(" \t", at, at, null).FormatDetails());
    }
}
