using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TempArtifactReapResult(int RemovedFiles, int RemovedFolders, long BytesReclaimed,
    int RetainedFresh, int RetainedHeld, int Failed)
{
    internal string SummaryLine => $"temp-artifact-janitor removed_files={RemovedFiles} removed_folders={RemovedFolders} bytes={BytesReclaimed} retained_fresh={RetainedFresh} retained_held={RetainedHeld} failed={Failed}";
}

internal static partial class TempRootJanitor
{
    private static readonly Regex CaptureName = new(@"^mcg-acc-[0-9a-f]{32}\.(out|err)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DigestName = new(@"^mcg-worktree-digest-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static TempArtifactReapResult ReapLeakedTempArtifacts(string tempRoot,
        string orchestratorTempParent, TimeProvider clock, TimeSpan? minimumAge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(orchestratorTempParent);
        ArgumentNullException.ThrowIfNull(clock);
        var cutoff = clock.GetUtcNow().UtcDateTime - (minimumAge ?? TimeSpan.FromHours(1));
        var removedFiles = 0;
        var removedFolders = 0;
        long bytes = 0;
        var fresh = 0;
        var held = 0;
        var failed = 0;
        var locations = new[] { tempRoot, orchestratorTempParent,
            Path.Combine(orchestratorTempParent, "acceptance-capture"),
            Path.Combine(orchestratorTempParent, "worktree-digest") }
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var location in locations)
        {
            if (!Directory.Exists(location)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(location, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!CaptureName.IsMatch(Path.GetFileName(path))) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (NewestTime(info.CreationTimeUtc, info.LastWriteTimeUtc) > cutoff)
                        { fresh++; continue; }
                        var length = info.Length;
                        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        File.Delete(path);
                        removedFiles++;
                        bytes += length;
                    }
                    catch (IOException) { held++; }
                    catch (UnauthorizedAccessException) { held++; }
                    catch (Exception) { failed++; }
                }
                foreach (var path in Directory.EnumerateDirectories(location, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!DigestName.IsMatch(Path.GetFileName(path))) continue;
                    try
                    {
                        var info = new DirectoryInfo(path);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { failed++; continue; }
                        var newest = NewestTime(info.CreationTimeUtc, info.LastWriteTimeUtc);
                        long folderBytes = 0;
                        var files = new List<string>();
                        foreach (var entry in EnumerateSafeDescendants(path))
                        {
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                var child = new DirectoryInfo(entry);
                                newest = NewestTime(newest, child.CreationTimeUtc, child.LastWriteTimeUtc);
                            }
                            else
                            {
                                var child = new FileInfo(entry);
                                newest = NewestTime(newest, child.CreationTimeUtc, child.LastWriteTimeUtc);
                                folderBytes += child.Length;
                                files.Add(entry);
                            }
                        }
                        if (newest > cutoff) { fresh++; continue; }
                        var leases = new List<FileStream>();
                        try
                        {
                            foreach (var file in files)
                                leases.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None));
                        }
                        finally
                        {
                            foreach (var lease in leases) lease.Dispose();
                        }
                        var deletion = DeleteTree(path);
                        if (deletion.Status == TempRootJanitorDeleteStatus.Deleted)
                        {
                            removedFolders++;
                            bytes += folderBytes;
                        }
                        else if (deletion.Status == TempRootJanitorDeleteStatus.Failed)
                        {
                            if (IsSharingViolation(deletion.ExceptionHResult)) held++;
                            else failed++;
                        }
                    }
                    catch (IOException ex) when (IsSharingViolation(ex.HResult)) { held++; }
                    catch (Exception) { failed++; }
                }
            }
            catch (Exception) { failed++; }
        }
        return new TempArtifactReapResult(removedFiles, removedFolders, bytes, fresh, held, failed);
    }

    private static DateTime NewestTime(params DateTime[] times) => times.Max();

    private static bool IsSharingViolation(int? hresult) =>
        hresult is { } value && (value & 0xffff) is 32 or 33;

    private static IEnumerable<string> EnumerateSafeDescendants(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop(), "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Digest folder contains a reparse point.");
                yield return entry;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }
}
