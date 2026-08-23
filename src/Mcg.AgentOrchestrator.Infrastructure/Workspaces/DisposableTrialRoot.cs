using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class DisposableTrialRoot
{
    private readonly IWorkerIntegrityLabeler _integrityLabeler;
    private readonly ITrialProcessInventory _processInventory;
    private readonly bool _useContainedJob;

    public DisposableTrialRoot()
        : this(new IcaclsIntegrityLabeler(), new SystemTrialProcessInventory(), useContainedJob: true)
    {
    }

    internal DisposableTrialRoot(
        IWorkerIntegrityLabeler integrityLabeler,
        ITrialProcessInventory processInventory,
        bool useContainedJob = true)
    {
        _integrityLabeler = integrityLabeler;
        _processInventory = processInventory;
        _useContainedJob = useContainedJob;
    }

    public TrialRootLease Create(TrialRootRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.StartNew();
        var source = Path.GetFullPath(request.SourceRepositoryPath);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Trial clone source does not exist: '{source}'.");
        }

        var revision = RunGitRequired(source, "rev-parse", $"{request.BaseCommit}^{{commit}}").Output.Trim();
        var baseDirectory = Path.GetFullPath(request.BaseDirectory ?? DefaultBaseDirectory());
        ValidateBaseDirectory(source, baseDirectory);

        var name = SanitizeName(request.Name);
        var root = Path.Combine(baseDirectory, $"{name}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(baseDirectory);
            RunGitRequired(source, "clone", "--no-local", "--no-hardlinks", source, root);
            RunGitRequired(root, "checkout", "--detach", revision);
            RunGitRequired(root, "remote", "remove", "origin");
            AssertStandalone(root, revision);

            var childEnvironment = TrialRootEnvironment.Build(root, request.ExtraEnvironment);
            var containmentLevel = TrialContainmentLevel.None;
            if (OperatingSystem.IsWindows())
            {
                if (!_integrityLabeler.SetIntegrity(root, WorkerSandboxPreparer.LowInheritableLevel, recursive: true))
                {
                    throw new InvalidOperationException(
                        $"Failed to apply inheritable Low integrity label to trial root '{root}'.");
                }

                containmentLevel = _useContainedJob
                    ? TrialContainmentLevel.WindowsLowIntegrityNoBreakawayJob
                    : TrialContainmentLevel.None;
            }

            var protectedPaths = (request.ProtectedPaths ?? [source])
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(CaptureProtectedPath)
                .ToArray();

            started.Stop();
            return new TrialRootLease(
                root,
                baseDirectory,
                revision,
                childEnvironment,
                protectedPaths,
                _processInventory,
                _useContainedJob,
                EnsurePositive(started.Elapsed),
                containmentLevel);
        }
        catch
        {
            TryDeletePartialRoot(root);
            throw;
        }
    }

    private static string DefaultBaseDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "Low",
            "mcg-trials");

    private static string SanitizeName(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "trial" : name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }

        return string.IsNullOrWhiteSpace(value) ? "trial" : value;
    }

    private static void ValidateBaseDirectory(string source, string baseDirectory)
    {
        if (IsSameOrBelow(source, baseDirectory))
        {
            throw new InvalidOperationException("Trial base directory must be outside the source repository.");
        }

        for (var current = new DirectoryInfo(baseDirectory); current is not null; current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")))
            {
                throw new InvalidOperationException(
                    $"Trial base directory cannot be beneath a Git work tree: '{current.FullName}'.");
            }
        }
    }

    private static bool IsSameOrBelow(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static GitCli.GitResult RunGitRequired(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        if (!result.Succeeded || result.DrainTimedOut)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            throw new InvalidOperationException(detail.Trim());
        }

        return result;
    }

    private static void AssertStandalone(string root, string revision)
    {
        var gitDirectory = Path.Combine(root, ".git");
        if (!Directory.Exists(gitDirectory) || File.Exists(gitDirectory))
        {
            throw new InvalidOperationException("Trial clone does not own a standalone .git directory.");
        }

        AssertGitPathBelowRoot(root, RunGitRequired(root, "rev-parse", "--git-dir").Output.Trim(), "git directory");
        AssertGitPathBelowRoot(root, RunGitRequired(root, "rev-parse", "--git-common-dir").Output.Trim(), "git common directory");

        if (File.Exists(Path.Combine(gitDirectory, "objects", "info", "alternates")))
        {
            throw new InvalidOperationException("Trial clone uses an object alternates file.");
        }

        if (!string.IsNullOrWhiteSpace(RunGitRequired(root, "remote").Output))
        {
            throw new InvalidOperationException("Trial clone retained a Git remote.");
        }

        var head = RunGitRequired(root, "rev-parse", "HEAD").Output.Trim();
        if (!head.Equals(revision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Trial HEAD '{head}' does not match requested revision '{revision}'.");
        }

        for (var ancestor = Directory.GetParent(root); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (Directory.Exists(Path.Combine(ancestor.FullName, ".git")) ||
                File.Exists(Path.Combine(ancestor.FullName, ".git")))
            {
                throw new InvalidOperationException(
                    $"Trial root has a Git work-tree ancestor: '{ancestor.FullName}'.");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            AssertNoHardLinkedGitFiles(gitDirectory);
        }
    }

    private static void AssertGitPathBelowRoot(string root, string gitPath, string description)
    {
        var resolved = Path.IsPathRooted(gitPath)
            ? Path.GetFullPath(gitPath)
            : Path.GetFullPath(Path.Combine(root, gitPath));
        if (!TrialRootEnvironment.IsBelow(root, resolved))
        {
            throw new InvalidOperationException($"Trial {description} resolves outside the trial root: '{resolved}'.");
        }
    }

    private static void AssertNoHardLinkedGitFiles(string gitDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(gitDirectory, "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!NativeFileLinks.GetFileInformationByHandle(stream.SafeFileHandle, out var information))
            {
                throw new InvalidOperationException(
                    $"Could not verify the NTFS link count for '{file}' (Win32 {Marshal.GetLastWin32Error()}).");
            }

            if (information.NumberOfLinks > 1)
            {
                throw new InvalidOperationException($"Trial Git file is hard-linked: '{file}'.");
            }
        }
    }

    internal static ProtectedPathSnapshot CaptureProtectedPath(string path)
    {
        var root = Path.GetFullPath(path);
        var entries = new Dictionary<string, ProtectedFileFingerprint>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(root))
        {
            entries[root] = Fingerprint(root);
        }
        else if (Directory.Exists(root))
        {
            entries[root] = new ProtectedFileFingerprint(-1, "<directory>");
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                entries[Path.GetFullPath(directory)] = new ProtectedFileFingerprint(-1, "<directory>");
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                entries[Path.GetFullPath(file)] = Fingerprint(file);
            }
        }
        else
        {
            entries[root] = new ProtectedFileFingerprint(-2, "<missing>");
        }

        return new ProtectedPathSnapshot(root, entries);
    }

    private static ProtectedFileFingerprint Fingerprint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new ProtectedFileFingerprint(stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    private static TimeSpan EnsurePositive(TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? elapsed : TimeSpan.FromTicks(1);

    private static void TryDeletePartialRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // The original creation failure remains authoritative.
        }
    }

    private static class NativeFileLinks
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}

internal sealed partial class TrialRootLease
{
    public TrialProcessHandle Start(ProcessStartInfo command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_sync)
        {
            if (_teardownReport is not null)
            {
                throw new InvalidOperationException("Cannot start a process after the trial root has been destroyed.");
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Trial process containment requires Windows; creation-only leases report ContainmentLevel.None.");
            }

            if (!string.IsNullOrWhiteSpace(command.Arguments))
            {
                throw new InvalidOperationException("Trial commands must use ProcessStartInfo.ArgumentList.");
            }

            var launchId = Guid.NewGuid().ToString("N");
            var scriptPath = Path.Combine(HarnessStatePath, $"launch-{launchId}.ps1");
            var stdoutPath = Path.Combine(HarnessStatePath, $"launch-{launchId}.stdout.log");
            var stderrPath = Path.Combine(HarnessStatePath, $"launch-{launchId}.stderr.log");
            var script = BuildLaunchScript(command);
            File.WriteAllText(scriptPath, script);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RootPath
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.Environment.Clear();
            foreach (var pair in ChildEnvironment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            using var suspended = _useContainedJob
                ? OwnedProcessGroup.StartSuspendedContainedWithFileCapture(startInfo, stdoutPath, stderrPath)
                : OwnedProcessGroup.StartSuspendedWithFileCapture(startInfo, stdoutPath, stderrPath);
            suspended.Resume();
            var process = suspended.TransferOwnership();
            _processes.Add(new TrialOwnedProcess(suspended.Group, process));
            return new TrialProcessHandle(process, stdoutPath, stderrPath);
        }
    }

    private static string BuildLaunchScript(ProcessStartInfo command)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

        var arguments = string.Join(",", command.ArgumentList.Select(Quote));
        return DispatchProcessHost.DropToLowScript + Environment.NewLine +
            "$mcgTrialArgs=@(" + arguments + ")" + Environment.NewLine +
            "& " + Quote(command.FileName) + " @mcgTrialArgs" + Environment.NewLine +
            "exit $LASTEXITCODE" + Environment.NewLine;
    }
}
