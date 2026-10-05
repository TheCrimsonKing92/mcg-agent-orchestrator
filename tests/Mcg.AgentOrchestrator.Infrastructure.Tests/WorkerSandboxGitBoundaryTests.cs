using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: each test owns its directories and labeler; no static overrides or real labels.
public sealed class WorkerSandboxGitBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplySandbox_NonLinkedCheckout_RefusesBeforeLabelWrites(bool subdirectory)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SandboxFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Worktree, ".git"));
        var workingDirectory = subdirectory
            ? Directory.CreateDirectory(Path.Combine(fixture.Worktree, "src", "nested")).FullName
            : fixture.Worktree;

        var refusal = Assert.Throws<WorkerSandboxRefusedException>(() => fixture.Apply(workingDirectory));

        Assert.Equal("writable-sandbox-outside-linked-worktree", refusal.ReasonCode);
        Assert.StartsWith($"WORKER_SANDBOX_REFUSED reason=writable-sandbox-outside-linked-worktree path={workingDirectory}", refusal.Message);
        Assert.Empty(fixture.Labeler.SetCalls);
        Assert.Empty(fixture.ProtectionCalls);
        Assert.Empty(fixture.Phases);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void ApplySandbox_LinkedCheckout_AllowsMediumMetadata(
        bool subdirectory, bool commonDirFile, bool absolutePaths)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SandboxFixture();
        fixture.CreateLinkedShape(commonDirFile, absolutePaths);
        var workingDirectory = subdirectory
            ? Directory.CreateDirectory(Path.Combine(fixture.Worktree, "src")).FullName
            : fixture.Worktree;

        fixture.Apply(workingDirectory);

        Assert.All(fixture.MetadataPaths, path => Assert.Contains(path, fixture.Labeler.QueryCalls));
        Assert.Equal(new[] { WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, WorkerSandboxPreparer.ProtectGitMetadataPhase }, fixture.ProtectionCalls);
        Assert.True(fixture.Phases.IndexOf("verify-git-metadata-integrity") >
            fixture.Phases.IndexOf(WorkerSandboxPreparer.ProtectGitMetadataPhase));
        Assert.True(fixture.Phases.IndexOf("materialize-sandbox") >
            fixture.Phases.IndexOf("verify-git-metadata-integrity"));
    }

    [Fact]
    public void ApplySandbox_NoGitAncestor_PreservesPreparation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SandboxFixture();

        fixture.Apply();

        Assert.Equal(new[] { fixture.Worktree, fixture.SandboxRoot }, fixture.Labeler.SetCalls.Select(call => call.Path));
        Assert.Empty(fixture.Labeler.QueryCalls);
        Assert.Contains("materialize-sandbox", fixture.Phases);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("hooks", false)]
    [InlineData("refs", true)]
    [InlineData("objects", false)]
    [InlineData("hooks", true)]
    public void ApplySandbox_UnsafeMetadata_RefusesBeforeMaterialization(string child, bool queryError)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SandboxFixture();
        fixture.CreateLinkedShape();
        var path = child.Length == 0 ? fixture.CommonDirectory : Path.Combine(fixture.CommonDirectory, child);
        fixture.Labeler.States[path] = new IntegrityLabelState(
            Exists: !queryError, Low: true, Inheritable: false, NativeQueryError: queryError ? 5 : null);

        var refusal = Assert.Throws<WorkerSandboxRefusedException>(() => fixture.Apply());

        Assert.Equal(queryError ? "shared-git-metadata-unverifiable" : "shared-git-metadata-low-writable", refusal.ReasonCode);
        Assert.Equal(path, refusal.Path);
        Assert.StartsWith($"WORKER_SANDBOX_REFUSED reason={refusal.ReasonCode} path={path}", refusal.Message);
        Assert.Contains(path, fixture.Labeler.QueryCalls);
        Assert.Contains(WorkerSandboxPreparer.ProtectGitMetadataPhase, fixture.ProtectionCalls);
        Assert.DoesNotContain("materialize-sandbox", fixture.Phases);
    }

    [Fact]
    public void ApplySandbox_ReceiptHit_StillRefusesLowObjects()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SandboxFixture();
        fixture.CreateLinkedShape();
        Directory.CreateDirectory(fixture.SandboxRoot);
        var coveredPhases = new[] { WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, WorkerSandboxPreparer.ProtectGitMetadataPhase };
        WorkerSandboxPreparer.WritePreparationFiles(fixture.Worktree, fixture.Worktree, fixture.SandboxRoot, coveredPhases);
        WorkerSandboxPreparer.WritePreparationFiles(fixture.SandboxRoot, fixture.Worktree, fixture.SandboxRoot, coveredPhases);
        var objects = Path.Combine(fixture.CommonDirectory, "objects");
        fixture.Labeler.States[objects] = new IntegrityLabelState(Exists: true, Low: true, Inheritable: true);

        var refusal = Assert.Throws<WorkerSandboxRefusedException>(() => fixture.Apply());

        Assert.Equal("shared-git-metadata-low-writable", refusal.ReasonCode);
        Assert.Equal(objects, refusal.Path);
        Assert.StartsWith($"WORKER_SANDBOX_REFUSED reason=shared-git-metadata-low-writable path={objects}", refusal.Message);
        Assert.Empty(fixture.Labeler.SetCalls);
        Assert.Empty(fixture.ProtectionCalls);
        Assert.Equal(new[] { "prepare-roots", "verify-git-metadata-integrity" }, fixture.Phases);
        Assert.Contains(objects, fixture.Labeler.QueryCalls);
    }

    [Fact]
    public void ApplySandbox_MissingMetadataChildren_SkipsTheirQueries()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SandboxFixture();
        fixture.CreateLinkedShape();
        foreach (var path in fixture.MetadataPaths.Skip(1))
        {
            Directory.Delete(path);
            fixture.Labeler.States[path] = new IntegrityLabelState(false, false, false, NativeQueryError: 2);
        }

        fixture.Apply();

        Assert.Equal(new[] { fixture.CommonDirectory }, fixture.Labeler.QueryCalls);
    }

    [Fact]
    public void ApplySandbox_ProtectionCompletes_VerifiesCurrentLabels()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SandboxFixture();
        fixture.CreateLinkedShape();
        var hooks = Path.Combine(fixture.CommonDirectory, "hooks");
        fixture.Labeler.States[hooks] = new IntegrityLabelState(true, true, false);

        fixture.Apply(afterProtection: () => fixture.Labeler.States[hooks] = new IntegrityLabelState(true, false, false, Medium: true));

        Assert.Contains(hooks, fixture.Labeler.QueryCalls);
        Assert.Contains("materialize-sandbox", fixture.Phases);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplySandbox_ReadOnlyOrOff_DoesNotApplyGitBoundary(bool lowIntegrity)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SandboxFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Worktree, ".git"));

        fixture.Apply(lowIntegrity: lowIntegrity, writable: !lowIntegrity);

        Assert.Empty(fixture.Labeler.QueryCalls);
        Assert.Empty(fixture.ProtectionCalls);
        Assert.DoesNotContain("verify-git-metadata-integrity", fixture.Phases);
        Assert.DoesNotContain(fixture.Labeler.SetCalls, call => call.Path == fixture.Worktree);
        Assert.Equal(lowIntegrity ? 1 : 0, fixture.Labeler.SetCalls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplySandbox_MalformedMetadata_RefusesAsUnverifiable(bool commonDirFile)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SandboxFixture();
        fixture.CreateLinkedShape();
        var path = commonDirFile
            ? Path.Combine(fixture.CommonDirectory, "worktrees", "wt", "commondir")
            : Path.Combine(fixture.Worktree, ".git");
        File.WriteAllText(path, "\n");

        var refusal = Assert.Throws<WorkerSandboxRefusedException>(() => fixture.Apply());

        Assert.Equal("shared-git-metadata-unverifiable", refusal.ReasonCode);
        Assert.Equal(path, refusal.Path);
        Assert.DoesNotContain("materialize-sandbox", fixture.Phases);
    }

    private sealed class SandboxFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "mcg-git-boundary", Guid.NewGuid().ToString("n"));
        public string Worktree { get; }
        public string SandboxRoot => Path.Combine(Worktree, ".mcg-sandbox");
        public string CommonDirectory { get; private set; }
        public string[] MetadataPaths => [CommonDirectory, .. new[] { "hooks", "refs", "objects" }.Select(child => Path.Combine(CommonDirectory, child))];
        public RecordingLabeler Labeler { get; } = new();
        public List<string> ProtectionCalls { get; } = [];
        public List<string> Phases { get; } = [];

        public SandboxFixture()
        {
            Worktree = Directory.CreateDirectory(Path.Combine(root, "worktree")).FullName;
            CommonDirectory = Path.Combine(root, "common");
        }

        public void CreateLinkedShape(bool commonDirFile = true, bool absolutePaths = false)
        {
            var gitDirectory = Directory.CreateDirectory(Path.Combine(CommonDirectory, "worktrees", "wt")).FullName;
            File.WriteAllText(Path.Combine(Worktree, ".git"), $"gitdir: {(absolutePaths ? gitDirectory : Path.GetRelativePath(Worktree, gitDirectory))}\n");
            if (commonDirFile)
            {
                File.WriteAllText(Path.Combine(gitDirectory, "commondir"), absolutePaths ? CommonDirectory : "../..\n");
            }
            else
            {
                CommonDirectory = gitDirectory;
            }
            foreach (var path in MetadataPaths) Directory.CreateDirectory(path);
        }

        public void Apply(string? workingDirectory = null, bool lowIntegrity = true, bool writable = true, Action? afterProtection = null)
        {
            workingDirectory ??= Worktree;
            var sandboxRoot = Path.Combine(workingDirectory, ".mcg-sandbox");
            Labeler.States[workingDirectory] = new IntegrityLabelState(true, true, true, Medium: true);
            Labeler.States[sandboxRoot] = new IntegrityLabelState(true, true, true, Medium: true);
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
            startInfo.Environment.Clear();
            startInfo.Environment["PATH"] = string.Empty;
            startInfo.ArgumentList.Add("Write-Output ok");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok", workingDirectory, Path.Combine(root, "out.log"), Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"), null, false, SandboxLowIntegrity: lowIntegrity, SandboxWorktreeWritable: writable);

            DispatchProcessHost.ApplyWorkerSandbox(startInfo, parameters, new WorkerSandboxPreparer(Labeler),
                (phase, _, _) => Phases.Add(phase),
                protectWorkspaceBoundary: _ => ProtectionCalls.Add(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase),
                protectGitMetadata: _ =>
                {
                    ProtectionCalls.Add(WorkerSandboxPreparer.ProtectGitMetadataPhase);
                    afterProtection?.Invoke();
                },
                providerEnvironmentReader: _ => null,
                gitMetadataLabeler: Labeler,
                sandboxFileSystem: new FixtureFileSystem(root));
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private sealed class RecordingLabeler : IWorkerIntegrityLabeler
    {
        public Dictionary<string, IntegrityLabelState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> QueryCalls { get; } = [];
        public List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];
        public IntegrityLabelState Query(string path)
        {
            QueryCalls.Add(path);
            return States.TryGetValue(path, out var state) ? state : new IntegrityLabelState(true, false, false, Medium: true);
        }
        public bool SetIntegrity(string path, string level, bool recursive)
        {
            SetCalls.Add((path, level, recursive));
            return true;
        }
    }

    // Prevent an operator's TEMP ancestry from supplying a checkout to these tests.
    private sealed class FixtureFileSystem(string root) : IWorkerSandboxFileSystem
    {
        private bool IsOwned(string path) => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        public bool FileExists(string path) => IsOwned(path) && File.Exists(path);
        public bool DirectoryExists(string path) => IsOwned(path) && Directory.Exists(path);
        public string ReadAllText(string path) => IsOwned(path)
            ? File.ReadAllText(path) : throw new InvalidOperationException("Read outside fixture root.");
    }
}
