using System.Globalization;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns a unique temporary directory.
public sealed class LaneTestDurationSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lane-snapshot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Read_PassedRows_ResolvesIdentityAndAppliesFloor()
    {
        var path = Write("durations.trx", ("Valid", "Passed", 2500), ("Failed", "Failed", 5000),
            ("Tiny", "Passed", 999), ("Floor", "Passed", 1000), ("Skipped", "NotExecuted", null));
        var snapshot = LaneTestDurationSnapshot.Read([path]);
        Assert.Null(snapshot.UnavailableReason);
        Assert.Equal(2, snapshot.Tests.Count);
        Assert.Equal(2500, snapshot.Tests["Example.GitTests.Valid"]);
        Assert.Equal(1000, snapshot.Tests["Example.GitTests.Floor"]);
    }

    [Fact]
    public void Read_TheoryCasesAndReruns_SumsWithinFileAndTakesLargestTotal()
    {
        var first = Write("first.trx", ("Theory(1)", "Passed", 600), ("Theory(2)", "Passed", 700));
        var second = Write("second.trx", ("Theory(3)", "Passed", 2500));
        Assert.Equal(1300, LaneTestDurationSnapshot.Read([first]).Tests["Example.GitTests.Theory"]);
        Assert.Equal(2500, LaneTestDurationSnapshot.Read([first, second]).Tests["Example.GitTests.Theory"]);
        Assert.Equal(2500, LaneTestDurationSnapshot.Read([second, first]).Tests["Example.GitTests.Theory"]);
    }

    [Fact]
    public void Read_MoreThan150Tests_KeepsLongest()
    {
        var path = Write("many.trx", Enumerable.Range(0, 160).Select(index =>
            ($"Test{index:000}", "Passed", (double?)(1000 + index))).ToArray());
        var snapshot = LaneTestDurationSnapshot.Read([path]);
        Assert.Equal(150, snapshot.Tests.Count);
        Assert.Equal(1159, snapshot.Tests.Values.Max());
        Assert.Equal(1010, snapshot.Tests.Values.Min());
    }

    [Theory]
    [InlineData("missing", "trx-missing:")]
    [InlineData("malformed", "trx-malformed:")]
    [InlineData("duration", "duration-missing:")]
    [InlineData("invalid", "duration-invalid:")]
    public void Read_BadFile_DiscardsEntireSnapshotWithReason(string kind, string reason)
    {
        var good = Write("good.trx", ("Valid", "Passed", 2500));
        var bad = Path.Combine(_root, "bad.trx");
        if (kind == "malformed") File.WriteAllText(bad, "<broken");
        if (kind == "duration") bad = Write("bad.trx", ("Missing", "Passed", null));
        if (kind == "invalid")
        {
            bad = Write("bad.trx", ("Invalid", "Passed", 2500));
            File.WriteAllText(bad, File.ReadAllText(bad).Replace("00:00:02.5000000", "invalid", StringComparison.Ordinal));
        }
        var snapshot = LaneTestDurationSnapshot.Read([good, bad]);
        Assert.Empty(snapshot.Tests);
        Assert.StartsWith(reason, snapshot.UnavailableReason);
        Assert.Contains("bad.trx", snapshot.UnavailableReason);
    }

    [Fact]
    public void Read_NoPaths_ReportsReason()
    {
        Assert.Equal("no-trx-paths", LaneTestDurationSnapshot.Read([]).UnavailableReason);
        Assert.Equal("no-trx-paths", LaneTestDurationSnapshot.Read(null).UnavailableReason);
    }

    [Fact]
    public void Read_UnreadableFile_ReturnsEmptyWithReason()
    {
        var path = Write("locked.trx", ("Valid", "Passed", 2500));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var snapshot = LaneTestDurationSnapshot.Read([path]);
        Assert.Empty(snapshot.Tests);
        Assert.StartsWith("trx-unreadable:", snapshot.UnavailableReason);
    }

    private string Write(string file, params (string Name, string Outcome, double? Ms)[] rows)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, file);
        new XDocument(new XElement("TestRun",
            new XElement("Results", rows.Select((row, index) => new XElement("UnitTestResult",
                new XAttribute("testId", index), new XAttribute("testName", row.Name), new XAttribute("outcome", row.Outcome),
                row.Ms is { } ms ? new XAttribute("duration", TimeSpan.FromMilliseconds(ms).ToString("c", CultureInfo.InvariantCulture)) : null))),
            new XElement("TestDefinitions", rows.Select((row, index) => new XElement("UnitTest", new XAttribute("id", index),
                new XElement("TestMethod", new XAttribute("className", "Example.GitTests"),
                    new XAttribute("name", row.Name.Split('(')[0]))))))).Save(path);
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
