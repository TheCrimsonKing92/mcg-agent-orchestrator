using System.Diagnostics;
using System.Runtime.CompilerServices;

// Core.Tests builds a self-contained Microsoft.Testing.Platform executable. When that
// executable runs from a Low-integrity-labeled acceptance worktree, it cannot write to
// the Medium-integrity system temp directory returned by Path.GetTempPath().
internal static class AssemblyTempRedirect
{
    internal const string LowInheritableLevel = "(OI)(CI)L";
    internal const string FixtureParentEnvironmentVariable = "MCG_CORE_TEST_TEMP_FIXTURE_PARENT";
    internal const string ChildHandshakeEnvironmentVariable = "MCG_CORE_TEST_TEMP_CHILD_HANDSHAKE";
    internal const string ChildReleaseEnvironmentVariable = "MCG_CORE_TEST_TEMP_CHILD_RELEASE";
    private static CoreOwnedTempRoot? processTempRoot;

    [ModuleInitializer]
    internal static void Install()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fileSystem = new PhysicalTempRootFileSystem();
        var labeler = new IcaclsTempRootIntegrityLabeler();
        var selection = SelectWritableRoot(
            EnumerateCandidateRoots(),
            candidate => TryPrepareRoot(
                candidate,
                isWindows: true,
                fileSystem,
                labeler,
                $".write-probe-{Guid.NewGuid():N}"));

        Console.Error.WriteLine(FormatDiagnostic(selection));
        if (selection.SelectedRoot is null)
        {
            return;
        }

        var ownedRoot = AssemblyTempRootOwnership.TryAcquireOwnedRoot(selection.SelectedRoot);
        if (ownedRoot is null)
        {
            Console.Error.WriteLine(
                $"assembly-temp-redirect lease=unavailable path=\"{AssemblyTempRootOwnership.RootLeasePath(selection.SelectedRoot)}\"");
            return;
        }

        processTempRoot = ownedRoot;
        Environment.SetEnvironmentVariable("TMP", selection.SelectedRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("TEMP", selection.SelectedRoot, EnvironmentVariableTarget.Process);

        AssemblyTempRootOwnership.ReapOrphanedRoots(selection.SelectedRoot, Console.Error.WriteLine);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var outcome = Interlocked.Exchange(ref processTempRoot, null)?.Release();
            if (outcome?.Status == CoreTempRootDeleteStatus.Failed)
            {
                Environment.ExitCode = 1;
            }
        };

        RunChildControlIfRequested(selection.SelectedRoot);
    }

    internal static TempRootSelectionResult SelectWritableRoot(
        IEnumerable<TempRootCandidate> candidates,
        Func<TempRootCandidate, TempRootPreparationResult> tryPrepareRoot)
    {
        var rejections = new List<TempRootRejection>();
        foreach (var candidate in candidates)
        {
            var preparation = tryPrepareRoot(candidate);
            if (preparation.Succeeded)
            {
                return new TempRootSelectionResult(candidate.Path, rejections);
            }

            rejections.Add(new TempRootRejection(candidate.Path, preparation.Reasons));
        }

        return new TempRootSelectionResult(null, rejections);
    }

    internal static TempRootPreparationResult TryPrepareRoot(
        TempRootCandidate candidate,
        bool isWindows,
        ITempRootFileSystem fileSystem,
        ITempRootIntegrityLabeler labeler,
        string probeDirectoryName)
    {
        try
        {
            fileSystem.CreateDirectory(candidate.Path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return TempRootPreparationResult.Rejected(TempRootRejectionReason.Create);
        }

        if (isWindows && candidate.RequiresLowLabel && !EnsureLowLabel(candidate.Path, labeler))
        {
            return TempRootPreparationResult.Rejected(TempRootRejectionReason.Label);
        }

        var reasons = new List<TempRootRejectionReason>();
        var probeDirectory = Path.Combine(candidate.Path, probeDirectoryName);
        var probeFile = Path.Combine(probeDirectory, "probe.tmp");
        try
        {
            fileSystem.CreateDirectory(probeDirectory);
            fileSystem.CreateProbeFile(probeFile);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            reasons.Add(TempRootRejectionReason.Write);
        }
        finally
        {
            var cleanupFailed = false;
            try
            {
                fileSystem.DeleteFile(probeFile);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                cleanupFailed = true;
            }

            try
            {
                fileSystem.DeleteDirectory(probeDirectory);
            }
            catch (DirectoryNotFoundException)
            {
                // No probe directory remains to poison a later lane.
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                cleanupFailed = true;
            }

            if (cleanupFailed)
            {
                reasons.Add(TempRootRejectionReason.Cleanup);
            }
        }

        return reasons.Count == 0
            ? TempRootPreparationResult.Success
            : new TempRootPreparationResult(false, reasons);
    }

    internal static string FormatDiagnostic(TempRootSelectionResult selection)
    {
        var selected = selection.SelectedRoot ?? "<none>";
        var rejected = selection.Rejections.Count == 0
            ? "<none>"
            : string.Join(
                "|",
                selection.Rejections.Select(rejection =>
                    $"{rejection.Path}:{string.Join('+', rejection.Reasons.Select(ReasonToken))}"));
        return $"assembly-temp-redirect selected={selected} rejected={rejected}";
    }

    private static bool EnsureLowLabel(string path, ITempRootIntegrityLabeler labeler)
    {
        try
        {
            var state = labeler.Query(path);
            if (state.Exists && state.Low && state.Inheritable)
            {
                return true;
            }

            if (!labeler.SetIntegrity(path, LowInheritableLevel, recursive: false))
            {
                return false;
            }

            state = labeler.Query(path);
            return state.Exists && state.Low && state.Inheritable;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is UnauthorizedAccessException or IOException;

    private static string ReasonToken(TempRootRejectionReason reason) => reason switch
    {
        TempRootRejectionReason.Create => "create",
        TempRootRejectionReason.Label => "label",
        TempRootRejectionReason.Write => "write",
        TempRootRejectionReason.Cleanup => "cleanup",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    internal static IEnumerable<TempRootCandidate> EnumerateCandidateRoots()
    {
        var fixtureParent = Environment.GetEnvironmentVariable(FixtureParentEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fixtureParent))
        {
            yield return new TempRootCandidate(
                AssemblyTempRootOwnership.BuildProcessTempRoot(fixtureParent, Environment.ProcessId),
                RequiresLowLabel: false);
            yield break;
        }

        foreach (var localAppData in new[]
                 {
                     Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return new TempRootCandidate(
                    AssemblyTempRootOwnership.BuildProcessTempRoot(
                        Path.Combine(localAppData, "Temp", "Low", "mcg-tests"),
                        Environment.ProcessId),
                    RequiresLowLabel: true);
            }
        }

        yield return new TempRootCandidate(
            AssemblyTempRootOwnership.BuildProcessTempRoot(
                Path.Combine(AppContext.BaseDirectory, ".test-tmp"),
                Environment.ProcessId),
            RequiresLowLabel: false);
    }

    private static void RunChildControlIfRequested(string selectedRoot)
    {
        var handshakePath = Environment.GetEnvironmentVariable(ChildHandshakeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(handshakePath) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FixtureParentEnvironmentVariable)))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(handshakePath)!);
            File.WriteAllText(handshakePath, selectedRoot);
            ChildControlOutputFlood.WriteIfRequested();
            var releasePath = Environment.GetEnvironmentVariable(ChildReleaseEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(releasePath))
            {
                return;
            }

            var releaseDirectory = Path.GetDirectoryName(releasePath)!;
            Directory.CreateDirectory(releaseDirectory);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(releasePath) && DateTime.UtcNow < deadline)
            {
                using var signal = new ManualResetEvent(initialState: false);
                signal.WaitOne(TimeSpan.FromMilliseconds(50));
            }

            if (!File.Exists(releasePath))
            {
                Environment.Exit(2);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"assembly-temp-child-control status=failed exceptionType={ex.GetType().Name}");
            Environment.Exit(2);
        }
    }
}

internal sealed record TempRootCandidate(string Path, bool RequiresLowLabel);

internal enum TempRootRejectionReason
{
    Create,
    Label,
    Write,
    Cleanup
}

internal sealed record TempRootPreparationResult(
    bool Succeeded,
    IReadOnlyList<TempRootRejectionReason> Reasons)
{
    internal static TempRootPreparationResult Success { get; } = new(true, []);

    internal static TempRootPreparationResult Rejected(TempRootRejectionReason reason) =>
        new(false, [reason]);
}

internal sealed record TempRootRejection(
    string Path,
    IReadOnlyList<TempRootRejectionReason> Reasons);

internal sealed record TempRootSelectionResult(
    string? SelectedRoot,
    IReadOnlyList<TempRootRejection> Rejections);

internal interface ITempRootFileSystem
{
    void CreateDirectory(string path);

    void CreateProbeFile(string path);

    void DeleteFile(string path);

    void DeleteDirectory(string path);
}

internal sealed class PhysicalTempRootFileSystem : ITempRootFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CreateProbeFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.WriteByte(0);
        stream.Flush(flushToDisk: true);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);
}

internal interface ITempRootIntegrityLabeler
{
    TempRootIntegrityLabelState Query(string path);

    bool SetIntegrity(string path, string level, bool recursive);
}

internal sealed record TempRootIntegrityLabelState(bool Exists, bool Low, bool Inheritable);

internal sealed class IcaclsTempRootIntegrityLabeler : ITempRootIntegrityLabeler
{
    private readonly Func<ProcessStartInfo, Process?> startProcess;

    public IcaclsTempRootIntegrityLabeler()
        : this(Process.Start)
    {
    }

    internal IcaclsTempRootIntegrityLabeler(Func<ProcessStartInfo, Process?> startProcess)
    {
        this.startProcess = startProcess;
    }

    public TempRootIntegrityLabelState Query(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return new TempRootIntegrityLabelState(Exists: false, Low: false, Inheritable: false);
        }

        try
        {
            var startInfo = CreateStartInfo(path);
            using var process = startProcess(startInfo);
            if (process is null)
            {
                return Unlabeled();
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000) || !Task.WaitAll([output, error], 2_000) || process.ExitCode != 0)
            {
                TryTerminate(process);
                return Unlabeled();
            }

            var text = output.Result;
            var low = text.Contains("Low Mandatory Level", StringComparison.OrdinalIgnoreCase) ||
                text.Contains(":(OI)(CI)(NW)", StringComparison.OrdinalIgnoreCase);
            var inheritable = text.Contains("(OI)", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("(CI)", StringComparison.OrdinalIgnoreCase);
            return new TempRootIntegrityLabelState(Exists: true, low, inheritable);
        }
        catch
        {
            return Unlabeled();
        }
    }

    public bool SetIntegrity(string path, string level, bool recursive)
    {
        try
        {
            var startInfo = CreateStartInfo(path);
            startInfo.ArgumentList.Add("/setintegritylevel");
            startInfo.ArgumentList.Add(level);
            if (recursive)
            {
                startInfo.ArgumentList.Add("/T");
            }

            using var process = startProcess(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000) || !Task.WaitAll([output, error], 2_000))
            {
                TryTerminate(process);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "icacls",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(path);
        return startInfo;
    }

    private static TempRootIntegrityLabelState Unlabeled() =>
        new(Exists: true, Low: false, Inheritable: false);

    private static void TryTerminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The exact child may already have exited between the timeout and termination.
        }
    }
}
