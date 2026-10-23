using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("ProcessSpawning")]
public sealed class DispatchProcessHostTestsGoToolCache
{
    [Xunit.Fact]
    public void LowIntegritySandboxPointsGoCachesInsideTheSandboxRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-go-cache-sandbox-test", Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(worktree);
        try
        {
            var startInfo = CreateStartInfo(worktree);
            // TEMP can live under the real profile; pin sibling child profile paths for the prefix check.
            startInfo.Environment["USERPROFILE"] = Path.Combine(root, "profile");
            startInfo.Environment["LOCALAPPDATA"] = Path.Combine(root, "profile", "AppData", "Local");
            var localAppDataBefore = startInfo.Environment["LOCALAPPDATA"]!;
            var userProfileBefore = startInfo.Environment["USERPROFILE"]!;
            var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(true, true, true));
            var parameters = CreateParameters(root, worktree, sandboxLowIntegrity: true);

            DispatchProcessHost.ApplyWorkerSandbox(
                startInfo,
                parameters,
                new WorkerSandboxPreparer(labeler),
                protectWorkspaceBoundary: _ => { });

            var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
            Assert.Contains((sandboxRoot, WorkerSandboxPreparer.LowInheritableLevel, false), labeler.SetCalls);
            AssertGoValues(startInfo.Environment, sandboxRoot);
            foreach (var name in new[] { "GOPATH", "GOMODCACHE", "GOCACHE" })
            {
                Assert.False(startInfo.Environment[name]!.StartsWith(localAppDataBefore, StringComparison.OrdinalIgnoreCase));
                Assert.False(startInfo.Environment[name]!.StartsWith(userProfileBefore, StringComparison.OrdinalIgnoreCase));
            }
            Assert.Equal(localAppDataBefore, startInfo.Environment["LOCALAPPDATA"]);
            Assert.Equal(Path.Combine(sandboxRoot, "temp"), startInfo.Environment["TEMP"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void SandboxOffBranchSetsTheSameGoVariables()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-go-cache-sandbox-off-test", Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(worktree);
        try
        {
            var startInfo = CreateStartInfo(worktree);
            var parameters = CreateParameters(root, worktree, sandboxLowIntegrity: false);

            var result = DispatchProcessHost.ApplyWorkerSandbox(startInfo, parameters);

            var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
            Assert.False(result.WorktreeRecursiveRelabel);
            Assert.False(result.SandboxRecursiveRelabel);
            AssertGoValues(startInfo.Environment, sandboxRoot);
            Assert.False(Directory.Exists(startInfo.Environment["GOPATH"]));
            Assert.False(Directory.Exists(startInfo.Environment["GOMODCACHE"]));
            Assert.False(Directory.Exists(startInfo.Environment["GOCACHE"]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string worktree)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = worktree
        };
        startInfo.ArgumentList.Add("Write-Output ok");
        startInfo.Environment["GOFLAGS"] = "-mod=mod";
        startInfo.Environment["GOTOOLCHAIN"] = "auto";
        startInfo.Environment["GOENV"] = @"C:\ambient-go-env";
        startInfo.Environment["GOPATH"] = @"C:\ambient-go";
        startInfo.Environment["GOMODCACHE"] = @"C:\ambient-modules";
        startInfo.Environment["GOCACHE"] = @"C:\ambient-cache";
        return startInfo;
    }

    private static DispatchProcessHost.DispatchRunParameters CreateParameters(
        string root, string worktree, bool sandboxLowIntegrity) =>
        new("Write-Output ok", worktree,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "exit.txt"),
            null,
            DisableSharedCompilation: false,
            SandboxLowIntegrity: sandboxLowIntegrity,
            Provider: WorkerSandboxProvider.Codex);

    private static void AssertGoValues(IDictionary<string, string?> environment, string sandboxRoot)
    {
        Assert.Equal(Path.Combine(sandboxRoot, "go"), environment["GOPATH"]);
        Assert.Equal(Path.Combine(sandboxRoot, "go", "pkg", "mod"), environment["GOMODCACHE"]);
        Assert.Equal(Path.Combine(sandboxRoot, "go-build"), environment["GOCACHE"]);
        Assert.Equal("local", environment["GOTOOLCHAIN"]);
        Assert.Equal("off", environment["GOENV"]);
        Assert.Equal("-modcacherw", environment["GOFLAGS"]);
    }

    private sealed class RecordingIntegrityLabeler(IntegrityLabelState queryState) : IWorkerIntegrityLabeler
    {
        public List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];

        public IntegrityLabelState Query(string path) => queryState;

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            SetCalls.Add((path, level, recursive));
            return true;
        }
    }
}
