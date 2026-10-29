using System.Diagnostics;
using System.Buffers.Binary;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Every case owns a unique root and child process; no ambient environment or real provider data.
public sealed class CodexScratchIntegrityPreparationTests
{
    [Fact]
    public void VerifiedAbsentLabelIsDistinctFromMalformedLabelAndCanBeRepaired()
    {
        var emptyAcl = new byte[8];
        emptyAcl[0] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(emptyAcl.AsSpan(2), 8);
        var absent = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(emptyAcl);
        Assert.True(absent.Unlabeled);
        emptyAcl[2] = 9;
        Assert.False(IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(emptyAcl).Unlabeled);
        RequireWindows();
        using var fixture = new ScratchFixture();
        var labels = new Labels { DefaultUnlabeled = true };
        using var scope = new WorkerSandboxPreparer(labels).OpenCodexScratch(fixture.Root, fixture.Sandbox);
        scope.ValidateAndRepair();
        Assert.True(labels.Query(fixture.Home).Low);
    }

    [Fact]
    public void NoPropagateLabelCannotAuthorizeMissingNestedScratch()
    {
        var acl = new byte[28];
        acl[0] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(2), 28);
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(4), 1);
        acl[8] = 0x11; // SYSTEM_MANDATORY_LABEL_ACE
        acl[9] = 0x07; // OI | CI | NP
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(10), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(acl.AsSpan(12), 1); // NO_WRITE_UP
        acl[16] = 1;
        acl[17] = 1;
        acl[23] = 16;
        BinaryPrimitives.WriteUInt32LittleEndian(acl.AsSpan(24), 4096);
        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(acl);
        Assert.True(state.Low);
        Assert.False(state.Inheritable);
    }

    [Fact]
    public void ReceiptHitRepairsMaterializedScratchAndHealthyReuseDoesNotMutate()
    {
        RequireWindows();
        using var fixture = new ScratchFixture();
        var labels = new Labels();
        var preparer = new WorkerSandboxPreparer(labels);
        _ = preparer.PrepareSandboxRootOnly(fixture.Root, fixture.Sandbox);
        Assert.True(preparer.PrepareSandboxRootOnly(fixture.Root, fixture.Sandbox).PrepReceiptHit);
        labels.Changes.Clear();
        var phases = new List<string>();
        var start = new ProcessStartInfo { WorkingDirectory = fixture.Root, CreateNoWindow = true };
        start.ArgumentList.Add("Write-Output unused");
        var parameters = Parameters(fixture, WorkerSandboxProvider.Codex);
        var materialized = false;
        var result = DispatchProcessHost.ApplyWorkerSandbox(start, parameters, preparer,
            (phase, _, _) => phases.Add(phase), seedCodexAuth: home =>
            {
                Assert.Equal(fixture.Home, home);
                fixture.CreateScratch();
                materialized = true;
            });
        Assert.True(materialized);
        Assert.True(result.PrepReceiptHit);
        foreach (var path in fixture.Targets) Assert.True(labels.Query(path).Low, path);
        Assert.True(phases.IndexOf("materialize-provider-seed") < phases.IndexOf("validate-codex-scratch"));
        Assert.All(labels.Changes, change => Assert.False(change.Recursive));
        Assert.DoesNotContain(labels.Changes, change => change.Path == fixture.Root);
        Assert.DoesNotContain(labels.Changes, change => change.Path.EndsWith("auth.json"));
        labels.Changes.Clear();
        using var scope = preparer.OpenCodexScratch(fixture.Root, fixture.Sandbox);
        scope.ValidateAndRepair();
        Assert.Empty(labels.Changes);
    }

    [Fact]
    public void MissingScratchUsesVerifiedHomeInheritanceWithoutCreatingDirectories()
    {
        RequireWindows();
        using var fixture = new ScratchFixture();
        var labels = new Labels();
        using var scope = new WorkerSandboxPreparer(labels).OpenCodexScratch(fixture.Root, fixture.Sandbox);
        scope.ValidateAndRepair();
        Assert.True(labels.Query(fixture.Home).Inheritable);
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, "tmp")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, "sessions")));
    }

    [Theory]
    [InlineData(WorkerSandboxProvider.Unknown, true)]
    [InlineData(WorkerSandboxProvider.Codex, false)]
    public void UnselectedOrDisabledSandboxDoesNotInspectCodex(WorkerSandboxProvider provider, bool enabled)
    {
        RequireWindows();
        using var fixture = new ScratchFixture();
        var labels = new Labels();
        var start = new ProcessStartInfo { WorkingDirectory = fixture.Root, CreateNoWindow = true };
        start.ArgumentList.Add("Write-Output unused");
        DispatchProcessHost.ApplyWorkerSandbox(start, Parameters(fixture, provider) with { SandboxLowIntegrity = enabled },
            new WorkerSandboxPreparer(labels), seedCodexAuth: _ => throw new InvalidOperationException("Unexpected Codex seed"));
        Assert.DoesNotContain(labels.Queries, path => path.Contains("codex-home"));
        Assert.False(Directory.Exists(fixture.Home));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedMutationOrReadbackRefusesBeforeLaunch(bool readback)
    {
        RequireWindows();
        using var fixture = new ScratchFixture();
        fixture.CreateScratch();
        var labels = new Labels { FailSet = !readback, FailReadback = readback };
        var launched = false;
        using var scope = new WorkerSandboxPreparer(labels).OpenCodexScratch(fixture.Root, fixture.Sandbox);
        var failure = Assert.Throws<WorkerSandboxRefusedException>(() =>
        {
            scope.ValidateAndRepair();
            launched = true;
        });
        Assert.Contains(readback ? "label-readback-failed" : "label-repair-failed", failure.ReasonCode);
        Assert.False(launched);
    }

    [Fact]
    public void RetainedHandlesPreventReplacementBeforeLabelMutation()
    {
        RequireWindows();
        using var fixture = new ScratchFixture();
        fixture.CreateScratch();
        var target = fixture.Targets.Last();
        Assert.True(File.Exists(target));
        using (var scope = new WorkerSandboxPreparer(new Labels()).OpenCodexScratch(fixture.Root, fixture.Sandbox))
        {
            Assert.Throws<IOException>(() => Directory.Move(fixture.Home, fixture.Home + "-replaced"));
            Assert.Throws<IOException>(() => File.Delete(target));
            Assert.Throws<IOException>(() => File.Move(target, target + "-replaced"));
            Assert.Throws<IOException>(() =>
            {
                using var writer = new FileStream(target, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            });
            Assert.True(File.Exists(target));
            scope.ValidateAndRepair();
        }
        File.Delete(target);
        Assert.False(File.Exists(target)); // The guard, rather than fixture permissions, denied deletion.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TraversalBudgetsRefuseBeforeMutation(bool depth)
    {
        RequireWindows();
        using var fixture = new ScratchFixture();
        var tmp = Path.Combine(fixture.Home, "tmp");
        Directory.CreateDirectory(tmp);
        if (depth)
        {
            for (var i = 0; i <= WorkerSandboxPreparer.CodexScratchScope.MaximumDepth; i++)
                Directory.CreateDirectory(tmp = Path.Combine(tmp, "d"));
        }
        else
        {
            for (var i = 0; i < WorkerSandboxPreparer.CodexScratchScope.MaximumEntries; i++)
                File.WriteAllText(Path.Combine(tmp, i + ".tmp"), "generated");
        }
        var labels = new Labels();
        var failure = Assert.Throws<WorkerSandboxRefusedException>(() =>
        {
            using var scope = new WorkerSandboxPreparer(labels).OpenCodexScratch(fixture.Root, fixture.Sandbox);
            scope.ValidateAndRepair();
        });
        Assert.Contains("scratch-budget-exceeded", failure.ReasonCode);
        Assert.Empty(labels.Changes);
    }

    [Fact]
    public async Task NativeSameLowTokenDeniesBeforeRepairAndWritesAfterRepair()
    {
        RequireWindows();
        using var fixture = new ScratchFixture(linkedGit: true);
        fixture.CreateScratch();
        var native = new IcaclsIntegrityLabeler();
        // Explicit labels make the before/after variable independent of the temp root's inheritance.
        Assert.True(native.SetIntegrity(fixture.Container, "(OI)(CI)M", recursive: true));
        var preparer = new WorkerSandboxPreparer(native);
        Assert.False(preparer.PrepareSandboxRootOnly(fixture.Root, fixture.Sandbox).RequiresRecovery);
        Assert.True(preparer.PrepareSandboxRootOnly(fixture.Root, fixture.Sandbox).PrepReceiptHit);
        Assert.True(native.SetIntegrity(fixture.Home, "(OI)(CI)M", recursive: true));
        Assert.True(native.Query(fixture.Home).Medium); // Parent-only baseline misses the defect.
        // Establish the excluded control independently of recursive inheritance in fixture setup.
        Assert.True(native.SetIntegrity(fixture.ExcludedFile, "M", recursive: false));
        var excludedLabelBeforeRepair = native.Query(fixture.ExcludedFile);
        Assert.True(excludedLabelBeforeRepair.Medium, $"Excluded control must start Medium: {excludedLabelBeforeRepair}");
        var protectedFiles = new[] { fixture.RepositoryFile, fixture.GitFile, fixture.SharedGitFile };
        var protectedContents = protectedFiles.ToDictionary(path => path, File.ReadAllText);
        foreach (var path in protectedFiles) Assert.True(native.Query(path).Medium, path);
        var scratchWrites = fixture.ProbeFiles.Concat(fixture.Targets.Take(4)
            .Select(path => Path.Combine(path, "created.probe"))).ToArray();
        var script = DispatchProcessHost.DropToLowScript + "\n" +
            "function Probe($p) { try { [IO.File]::WriteAllText($p,'probe'); 'allowed' } catch { if ($_.Exception.GetBaseException() -is [UnauthorizedAccessException]) { 'denied' } else { throw } } }\n" +
            "$paths=@(" + string.Join(",", scratchWrites.Select(Quote)) + ");\n" +
            "$protected=@(" + string.Join(",", protectedFiles.Select(Quote)) + ");\n" +
            "function Report($phase) { [ordered]@{ phase=$phase; pid=$PID; " +
            "scratch=@($paths | ForEach-Object { Probe $_ }); boundary=@($protected | ForEach-Object { Probe $_ }) } | ConvertTo-Json -Compress };\n" +
            "Report 'before'; $null=[Console]::ReadLine(); Report 'after'; $null=[Console]::ReadLine()";
        using var child = ProcessTreeGuiSuppression.Start(WorkerProcessRunner.BuildPowerShellStartInfo(script, fixture.Root));
        var errorDrain = child.StandardError.ReadToEndAsync();
        try
        {
            using var before = JsonDocument.Parse(await ReadEvent(child, "before-repair access results"));
            Assert.Equal("before", before.RootElement.GetProperty("phase").GetString());
            Assert.Equal(child.Id, before.RootElement.GetProperty("pid").GetInt32());
            Assert.True(WorkerBuildProcessIntegrity.IsLow(child));
            AssertResults(before, "scratch", "denied", scratchWrites.Length);
            AssertResults(before, "boundary", "denied", protectedFiles.Length);
            using (var scope = preparer.OpenCodexScratch(fixture.Root, fixture.Sandbox)) scope.ValidateAndRepair();
            foreach (var path in fixture.ProbeFiles) Assert.True(native.Query(path).Low, path);
            await child.StandardInput.WriteLineAsync("repair-verified");
            using var after = JsonDocument.Parse(await ReadEvent(child, "after-repair access results"));
            Assert.Equal("after", after.RootElement.GetProperty("phase").GetString());
            Assert.Equal(before.RootElement.GetProperty("pid").GetInt32(), after.RootElement.GetProperty("pid").GetInt32());
            Assert.True(WorkerBuildProcessIntegrity.IsLow(child));
            AssertResults(after, "scratch", "allowed", scratchWrites.Length);
            AssertResults(after, "boundary", "denied", protectedFiles.Length);
            await child.StandardInput.WriteLineAsync("probe-verified");
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(0, child.ExitCode);
            Assert.Equal("", await errorDrain);
            foreach (var path in protectedFiles)
            {
                Assert.True(native.Query(path).Medium, path);
                Assert.Equal(protectedContents[path], File.ReadAllText(path));
            }
            Assert.Equal("excluded", File.ReadAllText(fixture.ExcludedFile));
            Assert.Equal(excludedLabelBeforeRepair, native.Query(fixture.ExcludedFile));
            Console.WriteLine($"native scratch receipt: same Low pid={child.Id}; denied before, allowed after; repository/Git denied; excluded Medium");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await errorDrain.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task NativeLaunchReleasesScratchHandlesBeforePayloadExecution()
    {
        RequireWindows();
        using var fixture = new ScratchFixture();
        fixture.CreateScratch();
        var native = new IcaclsIntegrityLabeler();
        Assert.True(native.SetIntegrity(fixture.Container, "(OI)(CI)M", recursive: true));
        var preparer = new WorkerSandboxPreparer(native);
        Assert.False(preparer.PrepareSandboxRootOnly(fixture.Root, fixture.Sandbox).RequiresRecovery);
        Assert.True(preparer.PrepareSandboxRootOnly(fixture.Root, fixture.Sandbox).PrepReceiptHit);
        Assert.True(native.SetIntegrity(fixture.Home, "(OI)(CI)M", recursive: true));
        var temporary = Path.Combine(fixture.Sandbox, "temp");
        Directory.CreateDirectory(temporary);
        var script = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); " +
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);\n" +
            DispatchProcessHost.DropToLowScript + "\n$ErrorActionPreference='Stop';\n" +
            "$paths=@(" + string.Join(",", fixture.ProbeFiles.Select(Quote)) + ");\n" +
            "foreach ($p in $paths) { [IO.File]::WriteAllText($p,'payload-write') };\n" +
            "[ordered]@{ phase='payload'; pid=$PID; writes=$paths.Count } | ConvertTo-Json -Compress;\n" +
            "[Console]::WriteLine([Console]::In.ReadToEnd())";
        var start = WorkerProcessRunner.BuildPowerShellStartInfo(script, fixture.Root);
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "COMSPEC" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["PSModuleAnalysisCachePath"] = Path.Combine(temporary, "ModuleAnalysisCache");
        const string prompt = "scratch launch prompt \u03bb";
        var promptPath = Path.Combine(fixture.Container, "prompt.txt");
        File.WriteAllText(promptPath, prompt);
        using var scope = preparer.OpenCodexScratch(fixture.Root, fixture.Sandbox);
        Assert.Throws<IOException>(() => File.WriteAllText(fixture.ProbeFiles[0], "guard-precondition"));
        var atResume = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchTask = Task.Run(() => DispatchProcessHost.StartPreparedCodexWorker(start, scope, child =>
        {
            atResume.SetResult(child);
            allowResume.Task.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        }));
        // Propagate launch failures rather than waiting for a callback that can no longer arrive.
        _ = launchTask.ContinueWith(task => atResume.TrySetException(task.Exception!.InnerExceptions),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            var suspended = await atResume.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(suspended.HasExited);
            foreach (var path in fixture.ProbeFiles)
            {
                Assert.True(native.Query(path).Low, path);
                // This is the exact pre-resume boundary, held by an event, not a scheduling window.
                // Moving handle release after resume deterministically fails this sharing check.
                File.WriteAllText(path, "handles-released");
            }
        }
        catch
        {
            allowResume.TrySetResult();
            using var abandonedLaunch = await launchTask.WaitAsync(TimeSpan.FromSeconds(60));
            throw;
        }
        finally { allowResume.TrySetResult(); }
        using var launch = await launchTask.WaitAsync(TimeSpan.FromSeconds(60));
        var errors = launch.StandardError.ReadToEndAsync();
        var remainingOutput = Task.FromResult("");
        try
        {
            using var receipt = JsonDocument.Parse(await launch.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(60)) ?? throw new InvalidOperationException("Payload emitted no launch receipt."));
            Assert.Equal("payload", receipt.RootElement.GetProperty("phase").GetString());
            Assert.Equal(launch.Process.Id, receipt.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(fixture.ProbeFiles.Length, receipt.RootElement.GetProperty("writes").GetInt32());
            Assert.True(WorkerBuildProcessIntegrity.IsLow(launch.Process));
            foreach (var path in fixture.ProbeFiles) Assert.Equal("payload-write", File.ReadAllText(path));
            remainingOutput = launch.StandardOutput.ReadToEndAsync();
            DispatchProcessHost.WritePromptToWorkerStdin(launch.StandardInput,
                Parameters(fixture, WorkerSandboxProvider.Codex) with { PromptPath = promptPath });
            await launch.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(0, launch.Process.ExitCode);
            Assert.Equal(prompt + Environment.NewLine, await remainingOutput.WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.Equal("", await errors.WaitAsync(TimeSpan.FromSeconds(60)));
            Console.WriteLine("native launch receipt: suspended child; scratch handles released before resume; Low payload writes; UTF-8 stdin preserved");
        }
        finally
        {
            launch.Group.Kill();
            await launch.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await Task.WhenAll(errors, remainingOutput).WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public void NativeReparseRefusalLeavesExternalSentinelUntouched()
    {
        RequireWindows();
        using var fixture = new ScratchFixture();
        fixture.CreateScratch();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel");
        File.WriteAllText(sentinel, "external");
        var native = new IcaclsIntegrityLabeler();
        Assert.True(native.SetIntegrity(outside, "(OI)(CI)M", recursive: true));
        var link = Path.Combine(fixture.Home, "tmp", "external-link");
        var result = WorkerProcessRunner.RunBufferedAsync(new WorkerProcessRunRequest(
            "cmd /c mklink /J " + Quote(link) + " " + Quote(outside), fixture.Root, TimeSpan.FromSeconds(60)), CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(0, result.ExitCode);
        try
        {
            var failure = Assert.Throws<WorkerSandboxRefusedException>(() =>
            {
                using var scope = new WorkerSandboxPreparer(native).OpenCodexScratch(fixture.Root, fixture.Sandbox);
                scope.ValidateAndRepair();
            });
            Assert.Contains("reparse-target", failure.ReasonCode);
            Assert.Equal("external", File.ReadAllText(sentinel));
            Assert.True(native.Query(sentinel).Medium);
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeInjectedMutationOrReadbackFailureRefusesLaunch(bool failMutation)
    {
        RequireWindows();
        using var fixture = new ScratchFixture();
        fixture.CreateScratch();
        var native = new IcaclsIntegrityLabeler();
        Assert.True(native.SetIntegrity(fixture.Home, "(OI)(CI)M", recursive: true));
        var fault = new ReadbackFault(native, failMutation);
        var launched = false;
        using var scope = new WorkerSandboxPreparer(fault).OpenCodexScratch(fixture.Root, fixture.Sandbox);
        var refusal = Assert.Throws<WorkerSandboxRefusedException>(() =>
        {
            scope.ValidateAndRepair();
            launched = true;
        });
        Assert.Equal(!failMutation, fault.Mutated);
        Assert.Contains(failMutation ? "label-repair-failed" : "label-readback-failed-native-5", refusal.ReasonCode);
        Assert.False(launched);
        Console.WriteLine($"native repair receipt: injected {(failMutation ? "setter failure" : "readback error 5 after real mutation")}; launch refused");
    }

    private static async Task<string> ReadEvent(Process child, string expectedEvent)
    {
        try
        {
            return await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60))
                ?? throw new InvalidOperationException($"Low probe exited before {expectedEvent}.");
        }
        catch (TimeoutException failure)
        {
            throw new TimeoutException($"Low probe did not emit {expectedEvent} (hang guard).", failure);
        }
    }

    private static void AssertResults(JsonDocument receipt, string property, string expected, int count)
    {
        var results = receipt.RootElement.GetProperty(property).EnumerateArray().ToArray();
        Assert.Equal(count, results.Length);
        Assert.All(results, result => Assert.Equal(expected, result.GetString()));
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires native Windows ownership, handle and Low-token semantics.");
    }

    private static string Quote(string path) => "'" + path.Replace("'", "''") + "'";

    private static DispatchProcessHost.DispatchRunParameters Parameters(ScratchFixture fixture, WorkerSandboxProvider provider) =>
        new("Write-Output unused", fixture.Root, Path.Combine(fixture.Root, "out"), Path.Combine(fixture.Root, "err"),
            Path.Combine(fixture.Root, "exit"), null, DisableSharedCompilation: false,
            SandboxLowIntegrity: true, Provider: provider, SandboxWorktreeWritable: false);

    private sealed class Labels : IWorkerIntegrityLabeler
    {
        private readonly Dictionary<string, IntegrityLabelState> states = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Queries { get; } = [];
        internal List<(string Path, bool Recursive)> Changes { get; } = [];
        internal bool FailSet { get; init; }
        internal bool FailReadback { get; init; }
        internal bool DefaultUnlabeled { get; init; }
        public IntegrityLabelState Query(string path)
        {
            Queries.Add(path);
            return states.GetValueOrDefault(path) ?? new(true, false, !DefaultUnlabeled,
                Medium: !DefaultUnlabeled, Unlabeled: DefaultUnlabeled);
        }
        public bool SetIntegrity(string path, string level, bool recursive)
        {
            Changes.Add((path, recursive));
            if (FailSet) return false;
            if (!FailReadback) states[path] = new(true, true, level.Contains("(OI)(CI)"));
            return true;
        }
    }

    private sealed class ReadbackFault(IWorkerIntegrityLabeler native, bool failMutation) : IWorkerIntegrityLabeler
    {
        internal bool Mutated { get; private set; }
        public IntegrityLabelState Query(string path) => Mutated
            ? new(false, false, false, NativeQueryError: 5) : native.Query(path);
        public bool SetIntegrity(string path, string level, bool recursive) =>
            Mutated = !failMutation && native.SetIntegrity(path, level, recursive);
    }

    private sealed class ScratchFixture : IDisposable
    {
        internal string Container { get; } = Path.Combine(Path.GetTempPath(), "mcg-scratch-integrity-" + Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(Container, "worktree");
        internal string Sandbox => Path.Combine(Root, ".mcg-sandbox");
        internal string Home => Path.Combine(Sandbox, "codex-home");
        internal string RepositoryFile => Path.Combine(Root, "source.txt");
        internal string GitFile => Path.Combine(Root, ".git");
        internal string SharedGitFile => Path.Combine(Container, "shared-git", ".git", "config");
        internal string ExcludedFile => Path.Combine(Home, "auth.json");
        internal string[] Targets => [Home, Path.Combine(Home, "tmp"), Path.Combine(Home, "tmp", "arg0"),
            Path.Combine(Home, "sessions"), Path.Combine(Home, "sessions", "session.jsonl")];
        internal string[] ProbeFiles => [Path.Combine(Home, "tmp", "arg0", "alias.cmd"), Targets.Last()];
        internal ScratchFixture(bool linkedGit = false)
        {
            var main = Path.Combine(Container, "shared-git");
            Directory.CreateDirectory(main);
            if (linkedGit)
            {
                RunGit(main, "init");
                RunGit(main, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false",
                    "commit", "--allow-empty", "-m", "fixture");
                RunGit(main, "worktree", "add", "--detach", Root);
            }
            Directory.CreateDirectory(Sandbox);
            Directory.CreateDirectory(Path.GetDirectoryName(SharedGitFile)!);
            File.WriteAllText(RepositoryFile, "protected");
            if (!linkedGit)
                foreach (var path in new[] { GitFile, SharedGitFile }) File.WriteAllText(path, "protected");
        }
        internal void CreateScratch()
        {
            foreach (var path in Targets.Take(Targets.Length - 1)) Directory.CreateDirectory(path);
            foreach (var path in ProbeFiles) File.WriteAllText(path, "generated");
            File.WriteAllText(ExcludedFile, "excluded");
        }
        private static void RunGit(string directory, params string[] arguments)
        {
            var result = GitCli.RunForRepository(directory, arguments);
            Assert.True(result.Succeeded, result.Output + result.Error);
        }
        public void Dispose()
        {
            // Git objects may be read-only. Stay inside the claimed fixture; never traverse links.
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Container).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its temporary root.");
            ClearReadOnly(Container);
            Directory.Delete(Container, recursive: true);
        }
        private static void ClearReadOnly(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                if ((attributes & FileAttributes.Directory) != 0) ClearReadOnly(entry);
            }
        }
    }
}
