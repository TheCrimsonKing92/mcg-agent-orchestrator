using System.IO.Compression;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ExecutorToolsMainSourceTests
{
    [Fact]
    public void MaterializeUsesOnlyTrackedTopLevelScriptsAndReusesValidatedSha()
    {
        using var fixture = new Fixture();
        var source = Assert.IsType<ExecutorToolsSource>(fixture.Materialize());
        Assert.Equal(Fixture.Sha, source.MainSha);
        Assert.Equal(new[] { "A.ps1", "B.ps1" }, Directory.GetFiles(source.Folder).Select(Path.GetFileName).Order().ToArray());
        Assert.Equal(Fixture.A, File.ReadAllBytes(Path.Combine(source.Folder, "A.ps1")));
        Assert.Equal(Fixture.B, File.ReadAllBytes(Path.Combine(source.Folder, "B.ps1")));
        var control = Path.Combine(fixture.Root, "control");
        Directory.CreateDirectory(control);
        File.WriteAllBytes(Path.Combine(control, "A.ps1"), Fixture.A);
        File.WriteAllBytes(Path.Combine(control, "B.ps1"), Fixture.B);
        Assert.Equal(ExecutorToolsDigest.Compute(control), source.Digest);
        Assert.Equal("tools-staging-" + source.Digest[..12], Path.GetFileName(source.Folder));
        Assert.Equal(source, fixture.Materialize());
        Assert.Equal(1, fixture.Archives);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(source.Folder)!, "*.zip"));
        File.WriteAllText(Path.Combine(source.Folder, "A.ps1"), "tampered");
        Assert.Equal(source, fixture.Materialize());
        Assert.Equal(2, fixture.Archives);
        Assert.Equal(Fixture.A, File.ReadAllBytes(Path.Combine(source.Folder, "A.ps1")));
    }

    [Theory]
    [InlineData("rev-parse")]
    [InlineData("archive")]
    [InlineData("empty")]
    [InlineData("exception")]
    [InlineData("invalid-zip")]
    public void UnavailableTrackedSourceReturnsNull(string failure)
    {
        using var fixture = new Fixture { Failure = failure };
        Assert.Null(fixture.Materialize());
        Assert.Equal(failure == "rev-parse" || failure == "exception" ? 0 : 1, fixture.Archives);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Sha = "1234567890123456789012345678901234567890";
        internal static readonly byte[] A = [35, 32, 65, 10, 0, 255];
        internal static readonly byte[] B = [35, 32, 66, 13, 10];
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal string? Failure { get; init; }
        internal int Archives { get; private set; }
        internal ExecutorToolsSource? Materialize() => ExecutorToolsMainSource.Materialize(Root, "main", Root, Git);
        private GitCli.GitResult Git(string directory, int timeout, string[] args)
        {
            Assert.Equal(Root, directory);
            Assert.Equal(60_000, timeout);
            if (Failure == "exception") throw new IOException("fixture-git");
            if (args[0] == "rev-parse")
            {
                Assert.Equal(new[] { "rev-parse", "main" }, args);
                return new(Failure == "rev-parse" ? 1 : 0, Sha + "\n", "");
            }
            Archives++;
            Assert.Equal(new[] { "archive", "--format=zip", "-o" }, args[..3]);
            Assert.Equal(new[] { Sha, "scripts/executor/tools" }, args[4..]);
            if (Failure == "archive") return new(1, "", "fixture");
            if (Failure == "invalid-zip") { File.WriteAllText(args[3], "broken"); return new(0, "", ""); }
            using var zip = ZipFile.Open(args[3], ZipArchiveMode.Create);
            Write(zip, "scripts/executor/tools/readme.txt", [1]);
            Write(zip, "scripts/executor/tools/sub/C.ps1", [2]);
            if (Failure != "empty")
            {
                Write(zip, "scripts/executor/tools/A.ps1", A);
                Write(zip, "scripts/executor/tools/B.ps1", B);
            }
            return new(0, "", "");
        }
        private static void Write(ZipArchive archive, string name, byte[] bytes)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(bytes);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
