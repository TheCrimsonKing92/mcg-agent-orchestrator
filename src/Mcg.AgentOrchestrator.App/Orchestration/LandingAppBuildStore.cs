using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record LandingAppBuildRequest(string SourceRoot, string OutputDirectory, TimeSpan Timeout);
internal sealed record LandingAppBuildResult(int ExitCode, string Stdout, string Stderr, bool TimedOut = false);

internal sealed class LandingAppBuildFailedException(LandingAppBuildResult result)
    : InvalidOperationException(string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr)
{
    internal LandingAppBuildResult Result { get; } = result;
}

// Only completed, immutable App outputs belong in this store. File locks coordinate
// independent instances and processes; no correctness depends on process-local state.
internal sealed class LandingAppBuildStore
{
    internal const string AppDllName = "Mcg.AgentOrchestrator.App.dll";
    internal const string HeadMarkerName = AppDllName + ".git-head";
    internal const string CompleteMarkerName = ".complete";
    private readonly string _root;
    private readonly int _retention;
    private readonly Func<LandingAppBuildRequest, CancellationToken, LandingAppBuildResult> _build;
    private readonly Action<string>? _lockContended;

    internal LandingAppBuildStore(
        string root,
        int retention = 5,
        Func<LandingAppBuildRequest, CancellationToken, LandingAppBuildResult>? build = null,
        Action<string>? lockContended = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, 1);
        _root = Path.GetFullPath(root);
        _retention = retention;
        _build = build ?? ((request, ct) => ConductorSelfRelaunch.RunAppBuildProcess("dotnet", request, ct));
        _lockContended = lockContended;
    }

    internal static LandingAppBuildStore ForRepository(string repositoryRoot, string? dotnetPath = null)
    {
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(repositoryRoot))))[..16];
        return new LandingAppBuildStore(
            Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("landing-app-build"), key),
            build: (request, ct) => ConductorSelfRelaunch.RunAppBuildProcess(dotnetPath ?? "dotnet", request, ct));
    }

    internal string GetOrBuild(string sourceRoot, string sha, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // A sha is a single git object-id path segment, never a caller-supplied path.
        if (string.IsNullOrEmpty(sha) || !sha.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected a hexadecimal git object id.", nameof(sha));
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        string? partial = null;
        try
        {
            Directory.CreateDirectory(_root);
            using var shaLock = AcquireLock(sha + ".lock", timeout, elapsed, cancellationToken);
            var completed = Path.Combine(_root, sha);
            if (IsReusable(completed, sha)) return completed;

            if (Directory.Exists(completed)) Directory.Delete(completed, recursive: true);
            foreach (var stale in Directory.EnumerateDirectories(_root, sha + ".partial-*"))
                Directory.Delete(stale, recursive: true);

            partial = Path.Combine(_root, $"{sha}.partial-{Guid.NewGuid():N}");
            Directory.CreateDirectory(partial);
            var result = _build(new LandingAppBuildRequest(sourceRoot, partial, Remaining(timeout, elapsed)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || result.TimedOut) throw new LandingAppBuildFailedException(result);
            if (!File.Exists(Path.Combine(partial, AppDllName)))
                throw Failure($"App build output is missing {AppDllName}.");

            File.WriteAllText(Path.Combine(partial, HeadMarkerName), sha + Environment.NewLine);
            // Serialize publication, not compilation: stamps are strictly increasing even
            // across processes or a clock rollback, and retention sees a stable ordering.
            using var publicationLock = AcquireLock(".publication.lock", timeout, elapsed, cancellationToken);
            var entries = CompletedEntries();
            var stamp = Math.Max(DateTime.UtcNow.Ticks, entries.Select(entry => entry.Stamp).DefaultIfEmpty(0).Max() + 1);
            File.WriteAllText(Path.Combine(partial, CompleteMarkerName), stamp.ToString(CultureInfo.InvariantCulture));
            Directory.Move(partial, completed);
            partial = null;
            RetainNewest(completed);
            return completed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Failure(ex.Message);
        }
        finally
        {
            if (partial is not null && Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        }
    }

    private FileStream AcquireLock(string name, TimeSpan timeout, Stopwatch elapsed, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return File.Open(Path.Combine(_root, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                _lockContended?.Invoke(name);
                var remaining = Remaining(timeout, elapsed);
                var delay = remaining == Timeout.InfiniteTimeSpan ? 25 : Math.Min(25, Math.Max(1, remaining.TotalMilliseconds));
                if (ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(delay))) ct.ThrowIfCancellationRequested();
            }
        }
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xffff) is 32 or 33 or 11;

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch elapsed)
    {
        if (timeout == Timeout.InfiniteTimeSpan) return timeout;
        var remaining = timeout - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new LandingAppBuildFailedException(new(-1, "", "Timed out waiting for landed App build output.", true));
        return remaining;
    }

    private static LandingAppBuildFailedException Failure(string detail) => new(new(1, "", detail));

    private static bool IsReusable(string directory, string sha) =>
        File.Exists(Path.Combine(directory, AppDllName)) &&
        File.Exists(Path.Combine(directory, CompleteMarkerName)) &&
        File.Exists(Path.Combine(directory, HeadMarkerName)) &&
        File.ReadAllText(Path.Combine(directory, HeadMarkerName)).Trim().Equals(sha, StringComparison.Ordinal) &&
        long.TryParse(File.ReadAllText(Path.Combine(directory, CompleteMarkerName)), NumberStyles.None,
            CultureInfo.InvariantCulture, out _);

    private List<(string Directory, long Stamp)> CompletedEntries()
    {
        var entries = new List<(string Directory, long Stamp)>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var sha = Path.GetFileName(directory);
            if (!sha.All(Uri.IsHexDigit)) continue;
            try
            {
                if (IsReusable(directory, sha))
                    entries.Add((directory, long.Parse(File.ReadAllText(Path.Combine(directory, CompleteMarkerName)), CultureInfo.InvariantCulture)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return entries;
    }

    private void RetainNewest(string justCompleted)
    {
        try
        {
            var ordered = CompletedEntries().OrderByDescending(entry => entry.Stamp)
                .ThenBy(entry => Path.GetFileName(entry.Directory), StringComparer.Ordinal).ToArray();
            foreach (var entry in ordered.Skip(_retention))
            {
                if (entry.Directory.Equals(justCompleted, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var shaLock = File.Open(Path.Combine(_root, Path.GetFileName(entry.Directory) + ".lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    // Probe the dll before touching any payload; Windows successors may map it.
                    WholeDirectoryRemoval.Remove(entry.Directory, AppDllName);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class LandingAppBuildStoreCanaryBinaryResolver(LandingAppBuildStore store)
    : IPostLandingCanaryApplicationBinaryResolver
{
    public async Task<string> ResolveAsync(string sourceRoot, string sourceSha, CancellationToken cancellationToken)
    {
        try
        {
            var directory = await Task.Run(() => store.GetOrBuild(sourceRoot, sourceSha,
                Timeout.InfiniteTimeSpan, cancellationToken), cancellationToken).ConfigureAwait(false);
            return Path.Combine(directory, LandingAppBuildStore.AppDllName);
        }
        catch (LandingAppBuildFailedException ex)
        {
            var detail = (ex.Result.Stdout + Environment.NewLine + ex.Result.Stderr).Trim();
            if (detail.Length > 2000) detail = detail[^2000..];
            throw new PostLandingCanaryEvaluationException(
                $"Failed to build freshly landed main binary (exit {ex.Result.ExitCode}): {detail}");
        }
    }
}
