using Mcg.AgentOrchestrator.Infrastructure;

// Pins MCG_DOTNET_ISOLATED_ROOT to an ephemeral per-run root for the dotnet
// build-slot collection. Per-test EnvVarScope overrides nest cleanly on top of
// this baseline and the collection root is deleted only after the slot tests finish.
public sealed class IsolatedDotnetRootFixture : IDisposable
{
    internal static readonly TimeSpan LockLessRootStaleAge = TimeSpan.FromHours(24);
    internal const string OwnerLockSuffix = ".owner.lock";

    private readonly string? _originalValue;
    private readonly string _root;
    private readonly FileStream? _ownerLock;
    private readonly string? _ownerLockPath;
    private readonly Action<string> _diagnostic;

    internal string RootPath => _root;

    public IsolatedDotnetRootFixture() : this(GetWindowsBasePath(), null)
    {
    }

    internal IsolatedDotnetRootFixture(string? basePath, Action<string>? diagnostic)
    {
        _originalValue = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        _diagnostic = diagnostic ?? Console.Error.WriteLine;
        if (basePath is not null)
        {
            basePath = Path.GetFullPath(basePath);
            try
            {
                Directory.CreateDirectory(basePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Could not create the short isolated dotnet test root base '{basePath}'.",
                    ex);
            }

            ReclaimAbandonedRoots(basePath);
            (_root, _ownerLock) = ClaimRoot(basePath);
            _ownerLockPath = _root + OwnerLockSuffix;
        }
        else
        {
            _root = Directory.CreateTempSubdirectory("mdi-").FullName;
        }

        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _root);
    }

    private static string? GetWindowsBasePath()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(localAppData)
            ? Path.GetFullPath(Path.Combine(localAppData, "..", "LocalLow"))
            : null;
    }

    private static (string Root, FileStream OwnerLock) ClaimRoot(string basePath)
    {
        // Hold the owner lock before making the directory visible to another fixture.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = Path.Combine(basePath, $"mdi-{Guid.NewGuid():N}"[..20]);
            var claimPath = candidate + ".claim";
            FileStream claim;
            try
            {
                claim = new FileStream(claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException ex) when (IsClaimCollision(ex))
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Could not atomically claim short isolated dotnet test root '{candidate}'.",
                    ex);
            }

            try
            {
                if (Directory.Exists(candidate))
                {
                    continue;
                }

                FileStream ownerLock;
                try
                {
                    ownerLock = new FileStream(
                        candidate + OwnerLockSuffix, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                }
                catch (IOException ex) when (IsClaimCollision(ex))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(candidate);
                    return (candidate, ownerLock);
                }
                catch
                {
                    ownerLock.Dispose();
                    File.Delete(candidate + OwnerLockSuffix);
                    throw;
                }
            }
            finally
            {
                claim.Dispose();
                File.Delete(claimPath);
            }
        }

        throw new IOException("Could not claim a short isolated dotnet test root.");
    }

    private static bool IsClaimCollision(IOException exception) =>
        (exception.HResult & 0xffff) is 80 or 183;

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xffff) is 32 or 33;

    private void ReclaimAbandonedRoots(string basePath)
    {
        try
        {
            if ((File.GetAttributes(basePath) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            foreach (var candidate in Directory.EnumerateDirectories(
                         basePath, "mdi-*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
            {
                if (!Path.GetFileName(candidate).StartsWith("mdi-", StringComparison.Ordinal) ||
                    !string.Equals(Path.GetDirectoryName(Path.GetFullPath(candidate)), basePath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var lockPath = candidate + OwnerLockSuffix;
                    if (File.Exists(lockPath))
                    {
                        if ((File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        FileStream probe;
                        try
                        {
                            probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        }
                        catch (FileNotFoundException)
                        {
                            continue;
                        }
                        catch (IOException ex) when (IsSharingViolation(ex))
                        {
                            continue;
                        }

                        using (probe)
                        {
                            DeleteTreeWithoutFollowingLinks(candidate);
                        }
                        File.Delete(lockPath);
                    }
                    else if (DateTime.UtcNow - NewestFileTimeUtc(candidate) > LockLessRootStaleAge)
                    {
                        DeleteTreeWithoutFollowingLinks(candidate);
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    // Another fixture finished reclaiming this candidate first.
                }
                catch (Exception ex)
                {
                    WriteDiagnostic("reclaim", candidate, ex);
                }
            }
        }
        catch (Exception ex)
        {
            WriteDiagnostic("reclaim", basePath, ex);
        }
    }

    private static DateTime NewestFileTimeUtc(string root)
    {
        DateTime? newest = null;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    var modified = File.GetLastWriteTimeUtc(entry);
                    newest = newest is null || modified > newest ? modified : newest;
                }
            }
        }

        return newest ?? Directory.GetLastWriteTimeUtc(root);
    }

    private static void DeleteTreeWithoutFollowingLinks(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Refusing to delete reparse-point root '{root}'.");
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
                    DeleteTreeWithoutFollowingLinks(entry);
                }
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(root);
    }

    private void WriteDiagnostic(string phase, string root, Exception exception)
    {
        try
        {
            _diagnostic($"isolated-dotnet-root cleanup-failed phase={phase} root={root} exceptionType={exception.GetType().Name}");
        }
        catch
        {
            // Diagnostic sinks must not fail the test run.
        }
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _originalValue);
            _ownerLock?.Dispose();
            if (Directory.Exists(_root))
            {
                DeleteTreeWithoutFollowingLinks(_root);
            }

            if (_ownerLockPath is not null)
            {
                File.Delete(_ownerLockPath);
            }
        }
        catch (Exception ex)
        {
            WriteDiagnostic("dispose", _root, ex);
        }
    }
}
