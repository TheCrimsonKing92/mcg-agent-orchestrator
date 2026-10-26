// Parallel-safe: every case owns a unique fixture root and reads its immutable snapshot.
public sealed class SourceSnapshotLineViewTests
{
    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\nb\n")]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\rb\r")]
    [InlineData("a\r\n\r\nb")]
    [InlineData("a\n\n")]
    [InlineData("a\r\r\nb")]
    [InlineData("\r\n")]
    [InlineData("\n\n")]
    [InlineData("")]
    public void GetLinesMatchesStringReaderSplitting(string text)
    {
        using var fixture = new SourceSnapshotFixture();
        var path = fixture.Write("src/A/File.cs", text);
        var lines = RepositorySourceSnapshotCache.Get(fixture.Root).GetLines(path);
        Assert.IsType<SourceSnapshotLineView>(lines);
        using var reader = new StringReader(File.ReadAllText(path));
        var reference = new List<string>();
        while (reader.ReadLine() is { } line) reference.Add(line);

        Assert.Equal(reference, lines);
        Assert.Equal(File.ReadAllLines(path), lines);
        Assert.Equal(reference.Count, lines.Count);
        for (var index = 0; index < reference.Count; index++) Assert.Equal(reference[index], lines[index]);
    }

    [Fact]
    public void SnapshotServesLinesFromAnOffsetIndexWithoutStoredLineStrings()
    {
        using var fixture = new SourceSnapshotFixture();
        var path = fixture.Write("src/A/File.cs", string.Join("\r\n", Enumerable.Range(1, 10000).Select(index => $"// line {index}")));
        var snapshot = RepositorySourceSnapshotCache.Get(fixture.Root);
        var lines = snapshot.GetLines(path);

        Assert.IsType<SourceSnapshotLineView>(lines);
        Assert.Same(lines, snapshot.GetLines(path));
        Assert.Equal(10000, lines.Count);
        var firstRead = lines[4320];
        var secondRead = lines[4320];
        Assert.Equal("// line 4321", firstRead);
        Assert.Equal(firstRead, secondRead);
        Assert.NotSame(firstRead, secondRead);
        var list = Assert.IsAssignableFrom<IList<string>>(lines);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add("new line"));
        Assert.Throws<NotSupportedException>(() => list.Insert(0, "new line"));
        Assert.Throws<NotSupportedException>(() => list.Remove("// line 1"));
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list[0] = "new line");
        Assert.Throws<ArgumentOutOfRangeException>(() => lines[10000]);
        Assert.Throws<ArgumentOutOfRangeException>(() => lines[-1]);
    }
}
