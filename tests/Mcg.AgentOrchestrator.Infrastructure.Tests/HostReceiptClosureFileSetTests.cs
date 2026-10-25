using System.Globalization;
using System.Text.RegularExpressions;

// Parallel-safe: every fact owns its temporary generation and never writes the live host folder.
public sealed class HostReceiptClosureFileSetTests
{
    [Fact]
    public void ProbeMismatchNamesUnexpectedFileAndLength()
    {
        using var fixture = new GenerationFixture();
        var probe = Path.Combine(".test-tmp", "p1a2b", "probe.tmp");
        fixture.Write(probe, [1, 2, 3, 4, 5]);

        var difference = fixture.Compare();
        Assert.Empty(difference.Missing);
        Assert.Equal(new[] { probe }, difference.Unexpected);
        var message = Assert.Throws<InvalidOperationException>(() => fixture.RequireEqual()).Message;
        Assert.StartsWith("Host receipt closure file set mismatch.", message, StringComparison.Ordinal);
        Assert.Contains("missing=0", message, StringComparison.Ordinal);
        Assert.Contains("unexpected=1", message, StringComparison.Ordinal);
        Assert.Contains(probe + " (bytes=5,", message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingClosureIgnoresSealReceiptsAndPathCase()
    {
        using var fixture = new GenerationFixture();
        var recorded = new[] { "A.DLL", "A.deps.json", Path.Combine("runtimes", "win", "lib", "B.dll"), "a.dll" };

        fixture.RequireEqual(recorded);
        var difference = fixture.Compare(recorded);
        Assert.Empty(difference.Missing);
        Assert.Empty(difference.Unexpected);
    }

    [Fact]
    public void MissingRecordedFileIsNamed()
    {
        using var fixture = new GenerationFixture();
        File.Delete(Path.Combine(fixture.Root, "A.deps.json"));

        var difference = fixture.Compare();
        Assert.Equal(new[] { "A.deps.json" }, difference.Missing);
        Assert.Empty(difference.Unexpected);
        var message = Assert.Throws<InvalidOperationException>(() => fixture.RequireEqual()).Message;
        Assert.Contains("missing=1 [A.deps.json]", message, StringComparison.Ordinal);
        Assert.Contains("unexpected=0", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RootLevelUnexpectedFileIsNamed()
    {
        using var fixture = new GenerationFixture();
        fixture.Write("Extra.dll", [1, 2]);

        var message = Assert.Throws<InvalidOperationException>(() => fixture.RequireEqual()).Message;
        Assert.Contains("missing=0", message, StringComparison.Ordinal);
        Assert.Contains("unexpected=1 [Extra.dll (bytes=2,", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnexpectedSamplesAreOrderedBoundedAndCarryRealMetadata()
    {
        using var fixture = new GenerationFixture();
        var longName = "A" + new string('x', 145) + ".dll";
        Assert.Equal(150, longName.Length);
        var names = Enumerable.Range(0, 24).Select(index => $"F{index:D2}.dll").Append(longName)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        foreach (var name in names) fixture.Write(name, [1, 2, 3]);

        var difference = fixture.Compare();
        Assert.Equal(names, difference.Unexpected);
        var message = Assert.Throws<InvalidOperationException>(() => fixture.RequireEqual()).Message;
        Assert.Contains("unexpected=25", message, StringComparison.Ordinal);
        Assert.True(message.Length < 2500, message);
        var entries = ReadUnexpectedEntries(message);
        Assert.Equal(8, entries.Length);
        Assert.Equal(names.Take(8).Select(DisplayPath), entries.Select(entry => entry.Groups["path"].Value));
        foreach (var entry in entries)
        {
            var index = Array.IndexOf(entries, entry);
            var info = new FileInfo(Path.Combine(fixture.Root, names[index]));
            Assert.InRange(entry.Groups["path"].Value.Length, 1, 120);
            Assert.Equal(info.Length.ToString(CultureInfo.InvariantCulture), entry.Groups["bytes"].Value);
            Assert.Equal(info.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture), entry.Groups["created"].Value);
            Assert.Equal(info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture), entry.Groups["modified"].Value);
        }
        Assert.DoesNotContain(longName, message, StringComparison.Ordinal);
        foreach (var omitted in names.Skip(8)) Assert.DoesNotContain(omitted, message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothSidesKeepCountsAndEightSamplesWithinMessageBudget()
    {
        using var fixture = new GenerationFixture();
        var unexpected = Enumerable.Range(0, 25).Select(index => $"U{index:D2}" + new string('x', 143) + ".dll").ToArray();
        var missing = Enumerable.Range(0, 25).Select(index => $"M{index:D2}" + new string('y', 143) + ".dll").ToArray();
        foreach (var name in unexpected) fixture.Write(name, [1]);
        var recorded = fixture.Recorded.Concat(missing).ToArray();

        var difference = fixture.Compare(recorded);
        Assert.Equal(missing, difference.Missing);
        Assert.Equal(unexpected, difference.Unexpected);
        var message = Assert.Throws<InvalidOperationException>(() => fixture.RequireEqual(recorded)).Message;
        Assert.StartsWith("Host receipt closure file set mismatch.", message, StringComparison.Ordinal);
        Assert.True(message.Length < 2500, message);
        Assert.Contains("missing=25", message, StringComparison.Ordinal);
        Assert.Contains("unexpected=25", message, StringComparison.Ordinal);
        var missingSection = Regex.Match(message, @"missing=25 \[(?<paths>[^\]]+)\]").Groups["paths"].Value.Split("; ");
        Assert.Equal(8, missingSection.Length);
        var unexpectedEntries = ReadUnexpectedEntries(message);
        Assert.Equal(8, unexpectedEntries.Length);
        for (var index = 0; index < 8; index++)
        {
            AssertAbbreviation(missing[index], missingSection[index]);
            AssertAbbreviation(unexpected[index], unexpectedEntries[index].Groups["path"].Value);
            Assert.Equal("1", unexpectedEntries[index].Groups["bytes"].Value);
        }
    }

    [Fact]
    public void EqualSnapshotsHaveNoChangesRegardlessOfKeyCase()
    {
        var before = new Dictionary<string, int> { ["A.dll"] = 1, ["B.dll"] = 2 };
        var after = new Dictionary<string, int> { ["b.DLL"] = 2, ["a.DLL"] = 1 };

        Assert.Equal("", HostReceiptClosureFileSet.DescribeChanges(before, after));
    }

    [Fact]
    public void SnapshotChangesNameAddedRemovedAndChangedKeys()
    {
        var before = new Dictionary<string, int> { ["same.dll"] = 1, ["removed.dll"] = 2, ["changed.dll"] = 3 };
        var after = new Dictionary<string, int> { ["same.dll"] = 1, ["added.dll"] = 2, ["CHANGED.dll"] = 4 };

        Assert.Equal("added=1 [added.dll] removed=1 [removed.dll] changed=1 [changed.dll]",
            HostReceiptClosureFileSet.DescribeChanges(before, after));
    }

    [Fact]
    public void SnapshotSamplesAreSortedAndBoundedInEveryCategory()
    {
        var suffix = new string('x', 130) + ".dll";
        var added = Enumerable.Range(0, 12).Select(index => $"added{index:D2}" + suffix).Reverse().ToArray();
        var removed = Enumerable.Range(0, 12).Select(index => $"removed{index:D2}" + suffix).Reverse().ToArray();
        var changed = Enumerable.Range(0, 12).Select(index => $"changed{index:D2}" + suffix).Reverse().ToArray();
        var before = removed.Concat(changed).ToDictionary(path => path, _ => 1);
        var after = added.ToDictionary(path => path, _ => 1);
        foreach (var path in changed) after[path] = 2;

        var summary = HostReceiptClosureFileSet.DescribeChanges(before, after);
        var expected = new[] { (Label: "added", Paths: added), (Label: "removed", Paths: removed), (Label: "changed", Paths: changed) }
            .Select(category => category.Label + "=12 [" + string.Join("; ", category.Paths.OrderBy(path => path, StringComparer.Ordinal).Take(8).Select(DisplayPath)) + "]");
        Assert.Equal(string.Join(" ", expected), summary);
    }

    private static Match[] ReadUnexpectedEntries(string message) => Regex.Matches(message,
        @"(?:unexpected=\d+ \[|; )(?<path>[^;\[\]]+?) \(bytes=(?<bytes>\d+), createdUtc=(?<created>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z), lastWriteUtc=(?<modified>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z)\)")
        .Cast<Match>().ToArray();

    private static string DisplayPath(string path) => path.Length <= 120 ? path : path[..117] + "...";

    private static void AssertAbbreviation(string fullPath, string displayed)
    {
        Assert.InRange(displayed.Length, 4, 120);
        Assert.EndsWith("...", displayed, StringComparison.Ordinal);
        Assert.StartsWith(displayed[..^3], fullPath, StringComparison.Ordinal);
    }

    private sealed class GenerationFixture : IDisposable
    {
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal string SealPath => Path.Combine(Root, ".mcg-mtp-closure.json");
        internal string[] Recorded { get; } = ["A.dll", "A.deps.json", Path.Combine("runtimes", "win", "lib", "B.dll")];

        internal GenerationFixture()
        {
            foreach (var path in Recorded) Write(path, [1]);
            Write(".mcg-mtp-closure.json", [1]);
            Write(".mcg-build-receipt.txt", [1]);
            Write(".mcg-build-receipt.txt.tmp", [1]);
        }

        internal void Write(string relative, byte[] bytes)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        internal HostReceiptClosureFileSet.Comparison Compare(string[]? recorded = null) =>
            HostReceiptClosureFileSet.Compare(Root, SealPath, recorded ?? Recorded);

        internal void RequireEqual(string[]? recorded = null) =>
            HostReceiptClosureFileSet.RequireEqual(Root, SealPath, recorded ?? Recorded);

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
