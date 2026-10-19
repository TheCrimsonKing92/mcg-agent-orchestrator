using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: every fixture directory and byte sequence is test-owned.
public sealed class ExecutorToolsDigestTests : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Fact]
    public void Compute_TwoFiles_EqualsRawByteDefinition()
    {
        var directory = Fixture("tools", ("Z.ps1", new byte[] { 0xEF, 0xBB, 0xBF, 13, 10 }),
            ("a.PS1", new byte[] { 0, 1, 10 }));
        var lines = new[]
        {
            (Name: "z.ps1", Bytes: new byte[] { 0xEF, 0xBB, 0xBF, 13, 10 }),
            (Name: "a.ps1", Bytes: new byte[] { 0, 1, 10 })
        }.OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant() + "  " + file.Name + "\n");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)))).ToLowerInvariant();
        Assert.Equal(expected, ExecutorToolsDigest.Compute(directory));
    }

    [Xunit.Fact]
    public void Compute_CreationOrderAndNameCase_DoNotChangeDigest()
    {
        var first = Fixture("first", ("B.ps1", new byte[] { 2 }), ("A.ps1", new byte[] { 1 }));
        var second = Fixture("second", ("a.ps1", new byte[] { 1 }), ("b.ps1", new byte[] { 2 }));
        Assert.NotNull(ExecutorToolsDigest.Compute(first));
        Assert.Equal(ExecutorToolsDigest.Compute(first), ExecutorToolsDigest.Compute(second));
    }

    [Xunit.Theory]
    [Xunit.InlineData("a.ps1")]
    [Xunit.InlineData("b.ps1")]
    [Xunit.InlineData("add")]
    [Xunit.InlineData("rename")]
    public void Compute_ByteOrMembershipChange_ChangesDigest(string change)
    {
        var directory = Fixture("tools", ("a.ps1", new byte[] { 1 }), ("b.ps1", new byte[] { 2 }));
        var before = ExecutorToolsDigest.Compute(directory);
        if (change == "add") File.WriteAllBytes(Path.Combine(directory, "c.ps1"), [3]);
        else if (change == "rename") File.Move(Path.Combine(directory, "a.ps1"), Path.Combine(directory, "c.ps1"));
        else File.WriteAllBytes(Path.Combine(directory, change), [9]);
        Assert.NotNull(before);
        Assert.NotEqual(before, ExecutorToolsDigest.Compute(directory));
    }

    [Xunit.Fact]
    public void Compute_OtherExtensionsAndSubfolders_AreIgnored()
    {
        var directory = Fixture("tools", ("a.ps1", new byte[] { 1 }), ("b.ps1", new byte[] { 2 }));
        var before = ExecutorToolsDigest.Compute(directory);
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "ignored");
        File.WriteAllText(Path.Combine(directory, "x.ps1x"), "ignored");
        Directory.CreateDirectory(Path.Combine(directory, "bin"));
        File.WriteAllText(Path.Combine(directory, "bin", "inner.ps1"), "ignored");
        Assert.NotNull(before);
        Assert.Equal(before, ExecutorToolsDigest.Compute(directory));
    }

    [Xunit.Fact]
    public void Compute_MissingOrNoScripts_ReturnsNull()
    {
        Assert.Null(ExecutorToolsDigest.Compute(Path.Combine(_root, "missing")));
        Assert.Null(ExecutorToolsDigest.Compute(Fixture("empty")));
        Assert.Null(ExecutorToolsDigest.Compute(Fixture("text", ("notes.txt", new byte[] { 1 }))));
    }

    [Xunit.Fact]
    public void ReadManifest_UppercaseHexAndBom_ReturnsLowercase()
    {
        var digest = string.Concat(Enumerable.Repeat("0123456789ABCDEF", 4));
        Assert.Equal(digest.ToLowerInvariant(), ExecutorToolsDigest.ReadManifest("{\"digest\":\"" + digest + "\"}"));
        Assert.Equal(digest.ToLowerInvariant(), ExecutorToolsDigest.ReadManifest("\uFEFF{\"digest\":\"" + digest + "\",\"files\":[]}"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("{")]
    [Xunit.InlineData("[]")]
    [Xunit.InlineData("{}")]
    [Xunit.InlineData("{\"digest\":1}")]
    [Xunit.InlineData("{\"digest\":null}")]
    [Xunit.InlineData("{\"digest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [Xunit.InlineData("{\"digest\":\"gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg\"}")]
    [Xunit.InlineData("{\"Digest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    public void ReadManifest_InvalidShapeOrDigest_ReturnsNull(string? text) =>
        Assert.Null(ExecutorToolsDigest.ReadManifest(text));

    private string Fixture(string name, params (string Name, byte[] Bytes)[] files)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        foreach (var file in files) File.WriteAllBytes(Path.Combine(directory, file.Name), file.Bytes);
        return directory;
    }

    public void Dispose() => Directory.Delete(_root, true);
}
