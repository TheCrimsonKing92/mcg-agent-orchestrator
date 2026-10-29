using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class AcceptanceBareCommandResolutionTests
{
    [Fact]
    public void PathDirectoryOrderPrecedesExtensionOrder()
    {
        using var files = new ResolverFiles();
        var first = files.Directory("first");
        var second = files.Directory("second");
        var expected = files.File(first, "probe.cmd");
        files.File(second, "probe.exe");

        Assert.Equal(expected, Resolve("probe", $"{first};{second}", ".exe;.cmd", files.Root));
    }

    [Fact]
    public void ExtensionOrderAndCaseInsensitiveEnvironmentKeysAreHonored()
    {
        using var files = new ResolverFiles();
        files.File(files.Root, "probe.cmd");
        var expected = files.File(files.Root, "probe.exe");
        var environment = new Dictionary<string, string?>
        {
            ["pAtH"] = files.Root,
            ["pAtHeXt"] = ";.exe;;.cmd;"
        };

        Assert.Equal(expected, AcceptanceBareCommandResolver.Resolve("probe", environment, files.Root));
    }

    [Fact]
    public void QuotedAndRelativePathEntriesUseChildWorkingDirectory()
    {
        using var files = new ResolverFiles();
        var directory = files.Directory("command files");
        var expected = files.File(directory, "probe.cmd");

        Assert.Equal(expected, Resolve("probe", $"\"{directory}\"", ".cmd", files.Root));
        Assert.Equal(expected, Resolve("probe", "\"command files\"", ".cmd", files.Root));
    }

    [Fact]
    public void ExplicitEmptyEntrySearchesChildWorkingDirectory()
    {
        using var files = new ResolverFiles();
        var other = files.Directory("other");
        var expected = files.File(files.Root, "probe.cmd");

        Assert.Equal(expected, Resolve("probe", $"{other};", ".cmd", files.Root));
        Assert.Null(Resolve("probe", other, ".cmd", files.Root));
    }

    [Theory]
    [InlineData(null, ".cmd")]
    [InlineData("", ".cmd")]
    [InlineData("present", null)]
    [InlineData("present", "")]
    [InlineData("present", ";;")]
    public void MissingSearchVariablesDoNotInventDefaults(string? path, string? pathExt)
    {
        using var files = new ResolverFiles();
        files.File(files.Root, "probe.cmd");
        var environment = new Dictionary<string, string?>();
        if (path is not null) environment["PATH"] = path == "present" ? files.Root : path;
        if (pathExt is not null) environment["PATHEXT"] = pathExt;

        Assert.Null(AcceptanceBareCommandResolver.Resolve("probe", environment, files.Root));
    }

    [Fact]
    public void DirectoriesAndExtensionlessFilesAreNotMatches()
    {
        using var files = new ResolverFiles();
        files.Directory("probe.cmd");
        files.File(files.Root, "probe");
        var second = files.Directory("second");
        var expected = files.File(second, "probe.cmd");

        Assert.Null(Resolve("probe", files.Root, ".cmd", files.Root));
        Assert.Equal(expected, Resolve("probe", $"{files.Root};{second}", ".cmd", files.Root));
    }

    [Fact]
    public void InvalidPathEntryDoesNotHideLaterMatch()
    {
        using var files = new ResolverFiles();
        var expected = files.File(files.Root, "probe.cmd");

        Assert.Equal(expected, Resolve("probe", $"invalid\0path;{files.Root}", ".cmd", files.Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("probe.cmd")]
    [InlineData("relative/probe")]
    [InlineData("relative\\probe")]
    public void QualifiedNamesAreNotResolved(string command)
    {
        using var files = new ResolverFiles();
        files.File(files.Root, "probe.cmd");

        Assert.Null(Resolve(command, files.Root, ".cmd", files.Root));
    }

    [Fact]
    public void BuilderUsesConfiguredEnvironmentAndRewritesOnlyFirstArgumentOnWindows()
    {
        using var files = new ResolverFiles();
        var expected = files.File(files.Root, "probe.cmd");
        string[] arguments = ["probe", "probe"];
        var startInfo = GoalAcceptanceVerifierCaptureCustody.BuildAcceptanceProcessStartInfo(
            arguments, files.Root, null, null, false,
            environment =>
            {
                environment["PATH"] = files.Root;
                environment["PATHEXT"] = ".cmd";
            },
            true, out var resolved);

        Assert.Equal(new[] { "probe", "probe" }, arguments);
        Assert.Equal(files.Root, startInfo.Environment["PATH"]);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, resolved);
            Assert.Equal($"/c \"\"{expected}\" \"probe\"\"", startInfo.Arguments);
            var unchanged = GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(arguments, files.Root);
            Assert.Equal("/c \"\"probe\" \"probe\"\"", unchanged.Arguments);
        }
        else
        {
            Assert.Null(resolved);
            Assert.Equal("'probe' 'probe'", startInfo.ArgumentList.Last());
        }
    }

    private static string? Resolve(string command, string path, string pathExt, string workingDirectory) =>
        AcceptanceBareCommandResolver.Resolve(command, new Dictionary<string, string?>
        {
            ["PATH"] = path,
            ["PATHEXT"] = pathExt
        }, workingDirectory);

    private sealed class ResolverFiles : IDisposable
    {
        internal string Root { get; } = System.IO.Directory.CreateTempSubdirectory("mcg-command-resolution-").FullName;

        internal string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

        internal string File(string directory, string name)
        {
            var path = Path.Combine(directory, name);
            System.IO.File.WriteAllText(path, string.Empty);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
