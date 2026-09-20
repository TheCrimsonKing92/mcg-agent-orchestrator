using System.Diagnostics;
using System.Reflection;

public sealed class AssemblyTempRootChildProcessTests
{
    [Fact]
    public void NormalExitDeletesOwnedRootAndPreservesLiveSibling()
    {
        using var fixture = new ChildFixture();
        var siblingRoot = AssemblyTempRootOwnership.BuildProcessTempRoot(fixture.Root, 0x7ffffffe);
        Directory.CreateDirectory(siblingRoot);
        using var siblingLease = AssemblyTempRootOwnership.TryAcquireOwnedRoot(siblingRoot);
        Assert.NotNull(siblingLease);
        var child = fixture.Start(holdForRelease: true);
        var childRoot = fixture.WaitForHandshake(child);

        Assert.True(Directory.Exists(childRoot));
        File.WriteAllText(fixture.ReleasePath, "release");
        Assert.True(child.WaitForExit(15_000), fixture.ReadFailure(child));

        Assert.Equal(0, child.ExitCode);
        Assert.False(Directory.Exists(childRoot));
        Assert.True(Directory.Exists(siblingRoot));
    }

    [Fact]
    public void KilledProcessResidueIsReapedByNextStartup()
    {
        using var fixture = new ChildFixture();
        var killedChild = fixture.Start(holdForRelease: true);
        var killedRoot = fixture.WaitForHandshake(killedChild);
        Assert.True(Directory.Exists(killedRoot));
        killedChild.Kill(entireProcessTree: true);
        Assert.True(killedChild.WaitForExit(15_000));
        Assert.True(Directory.Exists(killedRoot));

        fixture.ResetControlFiles();
        var reaperChild = fixture.Start(holdForRelease: false);
        fixture.WaitForHandshake(reaperChild);
        Assert.True(reaperChild.WaitForExit(15_000), fixture.ReadFailure(reaperChild));

        Assert.Equal(0, reaperChild.ExitCode);
        Assert.False(Directory.Exists(killedRoot));
    }

    [Fact]
    public void TwoNormalRunsLeaveNoOwnedRootGrowth()
    {
        using var fixture = new ChildFixture();

        fixture.RunToNormalExit();
        var afterFirst = fixture.OwnedRootCount;
        fixture.ResetControlFiles();
        fixture.RunToNormalExit();

        Assert.Equal(0, afterFirst);
        Assert.Equal(afterFirst, fixture.OwnedRootCount);
    }

    private sealed class ChildFixture : IDisposable
    {
        private readonly List<Process> children = [];

        internal ChildFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"mcg-core-child-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            HandshakePath = Path.Combine(Root, "control", "handshake.txt");
            ReleasePath = Path.Combine(Root, "control", "release.txt");
        }

        internal string Root { get; }

        internal string HandshakePath { get; }

        internal string ReleasePath { get; }

        internal int OwnedRootCount => Directory.EnumerateDirectories(Root)
            .Select(Path.GetFileName)
            .Count(name => AssemblyTempRootOwnership.TryParseProcessTempRootName(name, out _));

        internal Process Start(bool holdForRelease)
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The managed test host process path is unavailable.");
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(assemblyPath);
            }
            startInfo.ArgumentList.Add("--list-tests");
            startInfo.ArgumentList.Add("--filter-class");
            startInfo.ArgumentList.Add(nameof(AssemblyTempRedirectTests));

            startInfo.Environment[AssemblyTempRedirect.FixtureParentEnvironmentVariable] = Root;
            startInfo.Environment[AssemblyTempRedirect.ChildHandshakeEnvironmentVariable] = HandshakePath;
            if (holdForRelease)
            {
                startInfo.Environment[AssemblyTempRedirect.ChildReleaseEnvironmentVariable] = ReleasePath;
            }
            else
            {
                startInfo.Environment.Remove(AssemblyTempRedirect.ChildReleaseEnvironmentVariable);
            }

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the Core.Tests child control process.");
            children.Add(process);
            return process;
        }

        internal string WaitForHandshake(Process child)
        {
            var controlDirectory = Path.GetDirectoryName(HandshakePath)!;
            Directory.CreateDirectory(controlDirectory);
            using var watcher = new FileSystemWatcher(controlDirectory, Path.GetFileName(HandshakePath))
            {
                EnableRaisingEvents = true
            };
            if (!File.Exists(HandshakePath))
            {
                watcher.WaitForChanged(WatcherChangeTypes.Created, TimeSpan.FromSeconds(15));
            }

            Assert.True(File.Exists(HandshakePath), ReadFailure(child));
            return File.ReadAllText(HandshakePath);
        }

        internal void RunToNormalExit()
        {
            var child = Start(holdForRelease: false);
            WaitForHandshake(child);
            Assert.True(child.WaitForExit(15_000), ReadFailure(child));
            Assert.Equal(0, child.ExitCode);
        }

        internal void ResetControlFiles()
        {
            File.Delete(HandshakePath);
            File.Delete(ReleasePath);
        }

        internal string ReadFailure(Process child)
        {
            if (!child.HasExited)
            {
                return "child did not exit before the bounded wait";
            }

            return $"exit={child.ExitCode}; stdout={child.StandardOutput.ReadToEnd()}; stderr={child.StandardError.ReadToEnd()}";
        }

        public void Dispose()
        {
            foreach (var child in children)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(10_000);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    child.Dispose();
                }
            }

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
