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

    [Fact]
    public void WaitForHandshakeToleratesChildStillWritingHandshake()
    {
        using var fixture = new ChildFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.HandshakePath)!);
        using var writer = new FileStream(fixture.HandshakePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var expected = fixture.Root;
        writer.Write(System.Text.Encoding.UTF8.GetBytes(expected));
        writer.Flush();

        Assert.Equal(expected, fixture.WaitForHandshake(Process.GetCurrentProcess(), writer.Dispose));
    }

    [Fact]
    public void ChildFloodingBothStreamsExitsWithinExistingWaitAndIsCapturedInFull()
    {
        using var fixture = new ChildFixture();
        var child = fixture.Start(holdForRelease: false, floodBytes: 1_048_576);
        fixture.WaitForHandshake(child);
        Assert.True(child.WaitForExit(15_000), fixture.ReadFailure(child));
        Assert.Equal(0, child.ExitCode);
        var capture = fixture.Capture(child);
        Assert.True(capture.StdoutBytes >= 1_048_576, fixture.ReadFailure(child));
        Assert.True(capture.StderrBytes >= 1_048_576, fixture.ReadFailure(child));
        Assert.Contains(ChildControlOutputFlood.OutputMarker, capture.Stdout);
        Assert.Contains(ChildControlOutputFlood.ErrorMarker, capture.Stderr);
    }

    private sealed class ChildFixture : IDisposable
    {
        private readonly List<Process> children = [];
        private readonly Dictionary<Process, ChildProcessOutputCapture> captures = [];

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

        internal Process Start(bool holdForRelease, int floodBytes = 0)
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
            if (floodBytes > 0)
                startInfo.Environment[ChildControlOutputFlood.EnvironmentVariable] = floodBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            else
                startInfo.Environment.Remove(ChildControlOutputFlood.EnvironmentVariable);
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
            captures.Add(process, new ChildProcessOutputCapture(process));
            children.Add(process);
            return process;
        }

        internal string WaitForHandshake(Process child, Action? onHandshakeStillOpen = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var controlDirectory = Path.GetDirectoryName(HandshakePath)!;
            Directory.CreateDirectory(controlDirectory);
            using var watcher = new FileSystemWatcher(controlDirectory, Path.GetFileName(HandshakePath))
            {
                EnableRaisingEvents = true
            };
            if (!File.Exists(HandshakePath))
            {
                watcher.WaitForChanged(WatcherChangeTypes.Created, deadline - DateTime.UtcNow);
            }

            Assert.True(File.Exists(HandshakePath), ReadFailure(child));
            while (true)
            {
                try
                {
                    using var stream = new FileStream(HandshakePath, FileMode.Open, FileAccess.Read, FileShare.None);
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    onHandshakeStillOpen?.Invoke();
                    Thread.Sleep(10);
                }
            }
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
            if (!captures.TryGetValue(child, out var capture))
                return "child has no redirected stream capture";
            var joined = child.HasExited && capture.JoinAfterExit();
            var (stdout, stderr, stdoutBytes, stderrBytes) = capture.Snapshot();
            if (!child.HasExited)
            {
                return $"child did not exit before the bounded wait; stdoutBytes={stdoutBytes}; stderrBytes={stderrBytes}; stdout={Tail(stdout)}; stderr={Tail(stderr)}";
            }

            return $"exit={child.ExitCode}; captureComplete={joined}; stdoutBytes={stdoutBytes}; stderrBytes={stderrBytes}; stdout={Tail(stdout)}; stderr={Tail(stderr)}";
        }

        internal (string Stdout, string Stderr, int StdoutBytes, int StderrBytes) Capture(Process child)
        {
            Assert.True(child.HasExited);
            var capture = captures[child];
            Assert.True(capture.JoinAfterExit(), "Child output capture did not complete within 5 seconds.");
            return capture.Snapshot();
        }

        private static string Tail(string value) => value.Length <= 8192 ? value : value[^8192..];

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
                    if (captures.TryGetValue(child, out var capture) && child.HasExited)
                        _ = capture.JoinAfterExit();
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
