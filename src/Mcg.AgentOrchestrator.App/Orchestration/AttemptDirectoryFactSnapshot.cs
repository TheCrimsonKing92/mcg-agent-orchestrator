namespace Mcg.AgentOrchestrator.App.Orchestration;

// Expected facts advance only for deletions made by this pass, never for external changes.
internal sealed class AttemptDirectoryFactSnapshot
{
    private readonly string _directory;
    private readonly string _goalId;
    private readonly Func<string, IEnumerable<string>> _enumerate;
    private readonly SortedSet<string> _paths;
    private IReadOnlyCollection<RetentionAttemptIdentity> _attempts;
    private string? _attemptRevision;
    private bool _preMutationVerified;
    private bool _tainted;

    private AttemptDirectoryFactSnapshot(string directory, string goalId,
        Func<string, IEnumerable<string>> enumerate)
    {
        _directory = Path.GetFullPath(directory);
        _goalId = goalId;
        _enumerate = enumerate;
        AttemptsReadable = StorageRetentionMaintenance.TryReadAttemptIdentities(
            _directory, goalId, out _attempts, out _);
        _attemptRevision = EvidenceRetentionPolicy.ComputeFactRevision(_attempts, []);
        _paths = ReadPaths();
        Revision = HashPaths(_attempts, _paths);
    }

    internal static AttemptDirectoryFactSnapshot Capture(string goalDirectory, string goalId,
        Func<string, IEnumerable<string>>? enumerate = null) =>
        new(goalDirectory, goalId, enumerate ??
            (directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)));

    internal string Revision { get; private set; }
    internal bool AttemptsReadable { get; }
    internal int Enumerations { get; private set; }
    internal int FullPathHashes { get; private set; }

    internal bool AttemptFactsUnchanged(IReadOnlyCollection<RetentionAttemptIdentity> attempts) =>
        AttemptsReadable && !_tainted &&
        (_attemptRevision ??= EvidenceRetentionPolicy.ComputeFactRevision(_attempts, [])).Equals(
            EvidenceRetentionPolicy.ComputeFactRevision(attempts, []), StringComparison.Ordinal);

    internal bool VerifyUnchanged()
    {
        try
        {
            var readable = StorageRetentionMaintenance.TryReadAttemptIdentities(
                _directory, _goalId, out var attempts, out _);
            var paths = ReadPaths();
            var revision = HashPaths(attempts, paths);
            var unchanged = readable && AttemptFactsUnchanged(attempts) &&
                _paths.SequenceEqual(paths, StringComparer.Ordinal);
            if (unchanged)
            {
                Revision = revision;
            }
            return unchanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal void RecordDeletion(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _paths.Remove(fullPath);
        if (Path.GetDirectoryName(fullPath)!.Equals(_directory, StringComparison.OrdinalIgnoreCase) &&
            fullPath.EndsWith(".attempt.json", StringComparison.OrdinalIgnoreCase))
        {
            var attemptId = Path.GetFileName(fullPath)[..^".attempt.json".Length];
            _attempts = _attempts.Where(attempt =>
                !attempt.AttemptId.Equals(attemptId, StringComparison.OrdinalIgnoreCase)).ToArray();
            _attemptRevision = null;
        }
    }

    internal bool TryPrepareDeletion(string path, long discoveryLength, DateTime discoveryWriteTime,
        Action<string>? beforeDeletion, out string reason)
    {
        reason = "retention-facts-changed-before-effect";
        if (_tainted)
        {
            return false;
        }
        if (!_preMutationVerified)
        {
            _preMutationVerified = true;
            if (!VerifyUnchanged())
            {
                _tainted = true;
                return false;
            }
        }

        beforeDeletion?.Invoke(path);
        if (!StorageRetentionMaintenance.TryReadAttemptIdentities(
            _directory, _goalId, out var attempts, out var refreshReason))
        {
            reason = refreshReason;
            return false;
        }
        if (!AttemptFactsUnchanged(attempts))
        {
            return false;
        }

        reason = "candidate-changed-before-effect";
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == discoveryLength &&
                file.LastWriteTimeUtc == discoveryWriteTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private SortedSet<string> ReadPaths()
    {
        Enumerations++;
        return new SortedSet<string>(_enumerate(_directory).Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
    }

    private string HashPaths(IReadOnlyCollection<RetentionAttemptIdentity> attempts, IEnumerable<string> paths)
    {
        FullPathHashes++;
        return EvidenceRetentionPolicy.ComputeFactRevision(attempts, paths);
    }
}
