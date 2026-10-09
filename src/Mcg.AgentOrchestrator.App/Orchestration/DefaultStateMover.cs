using System.Diagnostics;
using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the transition between a real repository-local tree and a recorded tree plus pointer.
internal sealed class DefaultStateMover
{
    private readonly string root;
    private readonly OrchestratorProjectRegistry registry;
    private readonly IConductorLockProbe lockProbe;
    private readonly Func<string, bool> stopPending;
    private readonly Action<string, string> copyFile;
    private readonly Func<string, string> hashFile;
    private readonly Action<string, string> createLink;
    private readonly Func<DateTimeOffset> utcNow;
    private string OldDirectory => Path.Combine(root, ".orchestrator");

    internal DefaultStateMover(string repositoryRoot, OrchestratorProjectRegistry registry,
        IConductorLockProbe? lockProbe = null, Func<string, bool>? stopPending = null,
        Action<string, string>? copyFile = null, Func<string, string>? hashFile = null,
        Action<string, string>? createLink = null, Func<DateTimeOffset>? utcNow = null)
    {
        root = DefaultProjectStateLocation.CanonicalRoot(repositoryRoot);
        this.registry = registry;
        this.lockProbe = lockProbe ?? new SystemConductorLockProbe();
        this.stopPending = stopPending ?? File.Exists;
        this.copyFile = copyFile ?? ((source, destination) => File.Copy(source, destination));
        this.hashFile = hashFile ?? HashFile;
        this.createLink = createLink ?? CreateCompatibilityLink;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal DefaultStateMoveResult Move(bool dryRun = false)
    {
        var timestamp = utcNow().ToUniversalTime();
        var location = new DefaultProjectStateLocation(root, DefaultProjectStateLocation.DestinationSubpath(root),
            ".orchestrator.backup-" + timestamp.ToString("yyyyMMddTHHmmssZ"), timestamp);
        var destination = Path.GetFullPath(Path.Combine(registry.DataRootDirectory, location.RelativeStateDirectory));
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        var backup = Path.Combine(root, location.BackupDirectoryName);
        var plan = new List<string>
        {
            $"Source: {OldDirectory}", $"Staging: {staging}", $"Destination: {destination}",
            $"Backup: {backup}", $"Registry: {registry.RegistryPath}", $"Junction: {OldDirectory} -> {destination}"
        };
        var refusals = new List<string>();
        var promoted = false;
        var backedUp = false;
        var committed = false;
        try
        {
            _ = location.ResolveStateDirectory(registry.DataRootDirectory);
            if (registry.GetDefaultStateLocation(root) is not null)
                refusals.Add(PointsAt(OldDirectory, destination) ? "Default state is already moved." : "Default state location is already recorded; use --undo.");
            if (!Directory.Exists(OldDirectory)) refusals.Add("Source state directory does not exist.");
            if (IsLink(OldDirectory)) refusals.Add("Source is an unexpected link or junction.");
            if (Exists(destination)) refusals.Add("Destination already exists.");
            if (Exists(backup)) refusals.Add("Timestamped backup already exists.");
            CheckConductor(OldDirectory, refusals);
            SortedDictionary<string, string>? before = null;
            (long Count, bool ActiveGoals, bool ActiveDispatches) board = default;
            if (Directory.Exists(OldDirectory) && !IsLink(OldDirectory))
            {
                before = Snapshot(OldDirectory);
                CheckNestedConductors(OldDirectory, before, refusals);
                board = ProbeBoard(OldDirectory, before);
                AddActiveRefusals(board, refusals);
            }
            if (dryRun || refusals.Count > 0) return new(plan, refusals);

            CopyTree(OldDirectory, staging, before!);
            VerifySnapshot(before!, Snapshot(staging));
            var copiedBoard = ProbeBoard(staging, before!);
            if (copiedBoard.Count != board.Count) throw new IOException("Copied state database goal count differs from source.");
            CheckConductor(OldDirectory, refusals);
            CheckNestedConductors(OldDirectory, before!, refusals);
            AddActiveRefusals(copiedBoard, refusals);
            VerifySnapshot(before!, Snapshot(OldDirectory));
            if (refusals.Count > 0) return new(plan, refusals);

            Directory.Move(staging, destination);
            promoted = true;
            Directory.Move(OldDirectory, backup);
            backedUp = true;
            registry.RecordDefaultStateLocation(location);
            committed = true;
            createLink(OldDirectory, destination);
            if (!PointsAt(OldDirectory, destination)) throw new IOException("Compatibility junction does not point to the recorded destination.");
            _ = LocalGitExclude.TryAppendEntries(root, [".orchestrator", ".orchestrator.backup-*/"]);
            plan.Add("Default state moved; original retained at the backup path.");
        }
        catch (Exception error)
        {
            refusals.Add($"Move failed: {error.Message}");
            try
            {
                if (backedUp)
                {
                    if (PointsAt(OldDirectory, destination)) Directory.Delete(OldDirectory); // Only the pointer.
                    Directory.Move(backup, OldDirectory);
                    backedUp = false;
                    if (committed) registry.ClearDefaultStateLocation(root);
                    plan.Add("Original state restored at the old location.");
                }
                if (promoted && !committed)
                    Directory.Move(destination, staging);
                if (committed) plan.Add($"Copied state retained: {destination}");
            }
            catch (Exception recoveryError)
            {
                refusals.Add($"Recovery failed: {recoveryError.Message}; backup: {backup}; copied state: {destination}");
            }
        }
        finally
        {
            RemoveTemporaryTree(staging, refusals);
        }
        return new(plan, refusals);
    }

    internal DefaultStateMoveResult Undo(bool dryRun = false)
    {
        var plan = new List<string>();
        var refusals = new List<string>();
        var staging = OldDirectory + ".undo-" + Guid.NewGuid().ToString("N");
        string? destination = null;
        var pointerRemoved = false;
        var restored = false;
        try
        {
            var location = registry.GetDefaultStateLocation(root);
            if (location is null) return new(plan, ["No default state move is recorded for this repository."]);
            destination = location.ResolveStateDirectory(registry.DataRootDirectory);
            var backup = Path.Combine(root, location.BackupDirectoryName);
            plan.AddRange([$"Moved state: {destination}", $"Backup: {backup}", $"Restore: {OldDirectory}",
                $"Staging: {staging}", $"Clear registry record: {registry.RegistryPath}",
                $"Remove junction: {OldDirectory}", $"Retain moved tree and backup: {destination}; {backup}"]);
            if (!PointsAt(OldDirectory, destination)) refusals.Add("Old location is not the recorded compatibility junction.");
            CheckConductor(destination, refusals);
            // A missing/unreadable moved store permits recovery from the backup. Active work does not.
            var source = destination;
            SortedDictionary<string, string> before;
            (long Count, bool ActiveGoals, bool ActiveDispatches) board;
            try
            {
                before = Snapshot(source);
                board = ProbeBoard(source, before);
            }
            catch (Exception error)
            {
                plan.Add($"Moved tree is unusable ({error.Message}); restore the backup.");
                source = backup;
                before = Snapshot(source);
                board = ProbeBoard(source, before);
                CheckConductor(backup, refusals);
            }
            AddActiveRefusals(board, refusals);
            CheckNestedConductors(source, before, refusals);
            if (dryRun || refusals.Count > 0) return new(plan, refusals);
            CopyTree(source, staging, before);
            VerifySnapshot(before, Snapshot(staging));
            var copyBoard = ProbeBoard(staging, before);
            if (copyBoard.Count != board.Count) throw new IOException("Restored state database goal count differs from source.");
            CheckConductor(destination, refusals);
            CheckConductor(source, refusals);
            CheckNestedConductors(source, before, refusals);
            AddActiveRefusals(copyBoard, refusals);
            VerifySnapshot(before, Snapshot(source));
            if (!PointsAt(OldDirectory, destination)) refusals.Add("Compatibility junction changed during undo.");
            if (refusals.Count > 0) return new(plan, refusals);
            Directory.Delete(OldDirectory); // Non-recursive: never delete the target.
            pointerRemoved = true;
            Directory.Move(staging, OldDirectory);
            restored = true;
            registry.ClearDefaultStateLocation(root);
            plan.Add("Default state restored to a real directory; registry record cleared.");
        }
        catch (Exception error)
        {
            refusals.Add($"Undo failed: {error.Message}");
            if (pointerRemoved && destination is not null)
            {
                try
                {
                    // Preserve the verified restored copy if clearing the registry failed.
                    if (restored)
                    {
                        var retained = OldDirectory + ".undo-retained-" + Guid.NewGuid().ToString("N");
                        Directory.Move(OldDirectory, retained);
                        plan.Add($"Restored copy retained: {retained}");
                    }
                    createLink(OldDirectory, destination);
                }
                catch (Exception recoveryError)
                {
                    refusals.Add($"Undo recovery failed: {recoveryError.Message}; moved state retained: {destination}");
                }
            }
        }
        finally
        {
            RemoveTemporaryTree(staging, refusals);
        }
        return new(plan, refusals);
    }

    private void CheckConductor(string stateDirectory, List<string> refusals)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root, root, null, registry) with
        {
            OrchestratorDirectory = stateDirectory
        };
        if (lockProbe.ActiveOwnerPid(workspace) is { } pid) refusals.Add($"Conductor lock is held by process {pid}.");
        if (stopPending(Path.Combine(root, ConductorBatchLoop.StopFileName))) refusals.Add("Conductor stop file is pending.");
    }

    private static void AddActiveRefusals((long Count, bool ActiveGoals, bool ActiveDispatches) board, List<string> refusals)
    {
        if (board.ActiveGoals) refusals.Add("Board has non-terminal goals.");
        if (board.ActiveDispatches) refusals.Add("Board has active dispatches.");
    }

    private void CheckNestedConductors(string source, SortedDictionary<string, string> snapshot, List<string> refusals)
    {
        foreach (var path in snapshot.Keys.Where(path =>
            Path.GetFileName(path) == "conduct-loop.lock" && Path.GetDirectoryName(path) is { Length: > 0 }))
            CheckConductor(Path.GetDirectoryName(Path.Combine(source, path))!, refusals);
    }

    private static (long Count, bool ActiveGoals, bool ActiveDispatches) ProbeBoard(
        string source, SortedDictionary<string, string> snapshot)
    {
        var databases = snapshot.Keys.Where(path => Path.GetFileName(path).Equals("state.db", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!databases.Contains("state.db", StringComparer.OrdinalIgnoreCase))
            throw new IOException("Source state database is missing.");
        long count = 0;
        var activeGoals = false;
        var activeDispatches = false;
        foreach (var database in databases)
        {
            var probe = Path.Combine(OrchestratorTempRoot.GetParent(), "mcg-default-state-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(probe);
                var detached = Path.Combine(probe, "state.db");
                File.Copy(Path.Combine(source, database), detached);
                foreach (var suffix in new[] { "-wal", "-journal" })
                    if (snapshot.ContainsKey(database + suffix)) File.Copy(Path.Combine(source, database + suffix), detached + suffix);
                long databaseCount;
                using (var connection = StateDbConnectionFactory.Open(detached, StateDbConnectionProfile.FastFailRead))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM goals";
                    databaseCount = Convert.ToInt64(command.ExecuteScalar());
                }
                var kernel = new SqliteOrchestratorStateRepository(detached).LoadAsync().GetAwaiter().GetResult();
                if (kernel.Goals.Count != databaseCount)
                    throw new IOException("State database contains unreadable goal snapshots; quiet board cannot be established.");
                count += databaseCount;
                activeGoals |= kernel.Goals.Any(goal => !goal.IsTerminal);
                activeDispatches |= kernel.Goals.Any(goal => goal.Tasks.Any(task =>
                    task.Status == WorkTaskStatus.Running || task.LastProcess is { IsRunning: true }));
            }
            finally
            {
                if (Directory.Exists(probe)) Directory.Delete(probe, recursive: true);
            }
        }
        return (count, activeGoals, activeDispatches);
    }

    private SortedDictionary<string, string> Snapshot(string directory)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(directory);
        while (pending.TryDequeue(out var current))
        {
            if (IsLink(current)) throw new IOException($"State tree contains a link or junction: {current}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (IsLink(entry)) throw new IOException($"State tree contains a link or junction: {entry}");
                var relative = Path.GetRelativePath(directory, entry);
                if (Directory.Exists(entry))
                {
                    result.Add(relative + Path.DirectorySeparatorChar, string.Empty);
                    pending.Enqueue(entry);
                }
                else result.Add(relative, hashFile(entry));
            }
        }
        return result;
    }

    private void CopyTree(string source, string destination, SortedDictionary<string, string> snapshot)
    {
        Directory.CreateDirectory(destination);
        foreach (var relative in snapshot.Keys.Where(path => path.EndsWith(Path.DirectorySeparatorChar)))
            Directory.CreateDirectory(Path.Combine(destination, relative));
        foreach (var relative in snapshot.Keys.Where(path => !path.EndsWith(Path.DirectorySeparatorChar)))
            copyFile(Path.Combine(source, relative), Path.Combine(destination, relative));
    }

    private static void VerifySnapshot(SortedDictionary<string, string> expected, SortedDictionary<string, string> actual)
    {
        if (!expected.SequenceEqual(actual)) throw new IOException("State tree hash verification failed or tree changed during copy.");
    }

    private static string HashFile(string path)
    {
        // A conductor lease permits readers but keeps a writable handle open. Sharing
        // that handle lets the snapshot reach the lock probe and report its owner.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsLink(string path) => new DirectoryInfo(path).LinkTarget is not null ||
        (Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null;

    private static bool PointsAt(string link, string destination)
    {
        var target = new DirectoryInfo(link).LinkTarget;
        return target is not null && DefaultProjectStateLocation.SameRoot(
            Path.GetFullPath(target, Path.GetDirectoryName(link)!), destination);
    }

    private static void RemoveTemporaryTree(string path, List<string> refusals)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception error) { refusals.Add($"Temporary copy cleanup failed: {path}: {error.Message}"); }
    }

    private static void CreateCompatibilityLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // cmd interprets its command tail. Reject expansion/metacharacters rather than evaluating paths as code.
        if (path.IndexOfAny(['"', '%', '!', '\r', '\n', '&', '|', '<', '>', '^']) >= 0 ||
            target.IndexOfAny(['"', '%', '!', '\r', '\n', '&', '|', '<', '>', '^']) >= 0)
            throw new IOException("Junction paths contain unsupported command characters.");
        start.Arguments = $"/d /v:off /c mklink /J \"{path}\" \"{target}\"";
        using var process = Process.Start(start) ?? throw new IOException("Could not start junction creation.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new IOException("Junction creation did not exit within 30 seconds.");
        }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException($"Junction creation failed ({process.ExitCode}): {error.Result.Trim()} {output.Result.Trim()}");
    }
}
