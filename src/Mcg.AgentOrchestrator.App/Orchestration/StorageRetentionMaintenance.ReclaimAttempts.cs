namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class StorageRetentionMaintenance
{
    private static bool IsReclaimableTerminalStatus(Mcg.AgentOrchestrator.Core.GoalStatus status) =>
        status is Mcg.AgentOrchestrator.Core.GoalStatus.Completed or
            Mcg.AgentOrchestrator.Core.GoalStatus.Cancelled or
            Mcg.AgentOrchestrator.Core.GoalStatus.Superseded;

    private static int SweepOperatorAttemptEntries(
        string directory, EvidenceArtifactFamily family, DateTimeOffset now,
        StorageRetentionReclaimOptions options, List<EvidenceRetentionDecision> decisions)
    {
        var deleted = 0;
        if (options.OwnerlessAttemptMaxAge is null)
        {
            decisions.Add(new EvidenceRetentionDecision(family, EvidenceRetentionAction.RetainedUndecidable,
                directory, null, EvidenceOwnerResolution.Unrecorded,
                "goal-directory-has-no-unique-full-id-owner"));
            return 0;
        }
        FileSystemInfo[] entries;
        try
        {
            var root = new DirectoryInfo(directory);
            root.Refresh();
            if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Operator attempt root is a reparse point.");
            entries = root.EnumerateFileSystemInfos().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            decisions.Add(new EvidenceRetentionDecision(family, EvidenceRetentionAction.DeferredLocked,
                directory, null, EvidenceOwnerResolution.Unrecorded,
                "operator-attempt-enumeration-failed", FailureExceptionType: ex.GetType().Name));
            return 0;
        }
        foreach (var entry in entries)
        {
            deleted += ReclaimAgedEntry(entry, family, null, EvidenceOwnerResolution.Unrecorded,
                "ownerless-operator-attempt-past-age", options.OwnerlessAttemptMaxAge, now, decisions);
        }
        TryRemoveEmptyAttemptDirectory(directory, family, null, decisions);
        return deleted;
    }

    private static int TryReclaimUnknownGoalDirectory(
        string directory, EvidenceArtifactFamily family, DateTimeOffset now,
        StorageRetentionReclaimOptions options, List<EvidenceRetentionDecision> decisions)
    {
        if (options.OwnerlessAttemptMaxAge is null)
        {
            decisions.Add(new EvidenceRetentionDecision(family, EvidenceRetentionAction.RetainedUndecidable,
                directory, null, EvidenceOwnerResolution.Unmatched,
                "goal-directory-has-no-unique-full-id-owner"));
            return 0;
        }
        return ReclaimAgedEntry(new DirectoryInfo(directory), family, null, EvidenceOwnerResolution.Unmatched,
            "unknown-goal-directory-past-age", options.OwnerlessAttemptMaxAge, now, decisions);
    }

    private static int ReclaimTerminalGoalAttempts(
        string directory, EvidenceArtifactFamily family, StorageRetentionGoal goal,
        IReadOnlyCollection<RetentionAttemptIdentity> attempts, DateTimeOffset now,
        StorageRetentionReclaimOptions options, List<EvidenceRetentionDecision> decisions,
        ISet<string> retainedAttemptIds)
    {
        if (options.TerminalGoalAttemptMaxAge is null) return 0;
        var deleted = 0;
        foreach (var attempt in attempts.OrderByDescending(item => item.AttemptId.Length))
        {
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos()
                    .Where(entry => IsAttemptUnitEntry(directory, entry, attempt, attempts))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                decisions.Add(new EvidenceRetentionDecision(family, EvidenceRetentionAction.DeferredLocked,
                    directory, goal.GoalId, EvidenceOwnerResolution.UniqueTerminal,
                    "terminal-attempt-enumeration-failed", attempt.AttemptId, attempt.Ordinal,
                    FailureExceptionType: ex.GetType().Name));
                continue;
            }
            if (entries.Length == 0) continue;
            // Legacy attempts have only flat files in the goal directory. Their
            // per-file retention policy must still run, including receipt and
            // byte-bound handling. This path reclaims directory-based attempts.
            if (!entries.Any(entry => entry is DirectoryInfo)) continue;
            // The attempt is one retention unit: a fresh member protects every member.
            var measurements = entries.Select(TryMeasureEntry).ToArray();
            if (measurements.Any(measurement => !measurement.Success))
            {
                retainedAttemptIds.Add(attempt.AttemptId);
                decisions.Add(new EvidenceRetentionDecision(family, EvidenceRetentionAction.DeferredLocked,
                    directory, goal.GoalId, EvidenceOwnerResolution.UniqueTerminal,
                    "terminal-attempt-measure-failed", attempt.AttemptId, attempt.Ordinal));
                continue;
            }
            if (now - measurements.Max(measurement => measurement.Newest) <=
                MaxAttemptGrace(options.TerminalGoalAttemptMaxAge.Value))
            {
                retainedAttemptIds.Add(attempt.AttemptId);
                continue;
            }
            var payloadDeleteFailed = false;
            foreach (var entry in entries.OrderBy(entry =>
                entry.Name.EndsWith(".attempt.json", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                if (payloadDeleteFailed && entry.Name.EndsWith(".attempt.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                var removed = ReclaimAgedEntry(entry, family, goal.GoalId,
                    EvidenceOwnerResolution.UniqueTerminal, "terminal-goal-attempt-past-age",
                    options.TerminalGoalAttemptMaxAge, now, decisions, attempt);
                deleted += removed;
                if (removed == 0)
                {
                    payloadDeleteFailed = true;
                    retainedAttemptIds.Add(attempt.AttemptId);
                }
            }
        }
        TryRemoveEmptyAttemptDirectory(directory, family, goal.GoalId, decisions);
        return deleted;
    }

    private static bool IsAttemptUnitEntry(
        string goalDirectory, FileSystemInfo entry, RetentionAttemptIdentity attempt,
        IReadOnlyCollection<RetentionAttemptIdentity> attempts)
    {
        var declaredOwners = attempts.Where(candidate => candidate.DeclaredPaths?.Any(path =>
            IsPathContainedBy(goalDirectory, path) && IsPathContainedBy(entry.FullName, path)) == true).ToArray();
        if (declaredOwners.Length > 0)
            return declaredOwners.Length == 1 &&
                declaredOwners[0].AttemptId.Equals(attempt.AttemptId, StringComparison.OrdinalIgnoreCase);
        var namedOwner = attempts.Where(candidate =>
                entry.Name.Equals(candidate.AttemptId, StringComparison.OrdinalIgnoreCase) ||
                entry.Name.StartsWith(candidate.AttemptId + ".", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.StartsWith(candidate.AttemptId + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.AttemptId.Length)
            .FirstOrDefault();
        return namedOwner?.AttemptId.Equals(attempt.AttemptId, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void TryRemoveEmptyAttemptDirectory(
        string directory, EvidenceArtifactFamily family, string? goalId,
        List<EvidenceRetentionDecision> decisions)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            decisions.Add(new EvidenceRetentionDecision(family, EvidenceRetentionAction.DeferredLocked,
                directory, goalId, EvidenceOwnerResolution.Unrecorded,
                "empty-attempt-directory-delete-failed", FailureExceptionType: ex.GetType().Name));
        }
    }

    private static int ReclaimAgedEntry(
        FileSystemInfo entry, EvidenceArtifactFamily family, string? goalId,
        EvidenceOwnerResolution owner, string reason, TimeSpan? ageLimit, DateTimeOffset now,
        List<EvidenceRetentionDecision> decisions, RetentionAttemptIdentity? attempt = null)
    {
        if (ageLimit is null) return 0;
        var measure = TryMeasureEntry(entry);
        if (!measure.Success)
        {
            decisions.Add(new EvidenceRetentionDecision(family, EvidenceRetentionAction.DeferredLocked,
                entry.FullName, goalId, owner, "attempt-measure-failed",
                attempt?.AttemptId, attempt?.Ordinal, FailureExceptionType: measure.ExceptionType));
            return 0;
        }
        if (now - measure.Newest <= MaxAttemptGrace(ageLimit.Value))
        {
            decisions.Add(new EvidenceRetentionDecision(family, EvidenceRetentionAction.Preserved,
                entry.FullName, goalId, owner, "within-grace-period", attempt?.AttemptId, attempt?.Ordinal));
            return 0;
        }
        var deletion = entry is DirectoryInfo
            ? TryDeleteAttemptDirectory(entry.FullName)
            : DeleteMeasuredFile(entry.FullName, measure.Bytes);
        decisions.Add(new EvidenceRetentionDecision(family,
            deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
            entry.FullName, goalId, owner, deletion.Success ? reason : "attempt-delete-failed",
            attempt?.AttemptId, attempt?.Ordinal, BytesAttempted: measure.Bytes,
            BytesReclaimed: deletion.Success ? measure.Bytes : 0,
            FailureExceptionType: deletion.ExceptionType));
        return deletion.Success ? 1 : 0;
    }

    private static TimeSpan MaxGrace(TimeSpan configured) =>
        configured > WorkerCompressionAge ? configured : WorkerCompressionAge;

    private static TimeSpan MaxAttemptGrace(TimeSpan configured) =>
        configured > AcceptanceArtifactMaxAge ? configured : AcceptanceArtifactMaxAge;

    private static (bool Success, long BytesAttempted, string? ExceptionType) DeleteMeasuredFile(
        string path, long bytes)
    {
        var result = TryDeleteExclusive(path);
        return (result.Success, bytes, result.ExceptionType);
    }

    private static (bool Success, long BytesAttempted, string? ExceptionType) TryDeleteAttemptDirectory(string path)
    {
        var measurement = TryMeasureEntry(new DirectoryInfo(path));
        if (!measurement.Success) return (false, 0, measurement.ExceptionType);
        try
        {
            Directory.Delete(path, recursive: true);
            return (true, measurement.Bytes, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, 0, ex.GetType().Name);
        }
    }

    private static (bool Success, DateTimeOffset Newest, long Bytes, string? ExceptionType) TryMeasureEntry(
        FileSystemInfo entry)
    {
        try
        {
            entry.Refresh();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                return (false, default, 0, nameof(IOException));
            if (entry is FileInfo file)
                return (true, file.LastWriteTimeUtc, file.Length, null);
            var newest = DateTimeOffset.MinValue;
            long bytes = 0;
            var any = false;
            foreach (var child in ((DirectoryInfo)entry).EnumerateFileSystemInfos())
            {
                any = true;
                var measured = TryMeasureEntry(child);
                if (!measured.Success) return measured;
                if (measured.Newest > newest) newest = measured.Newest;
                bytes = checked(bytes + measured.Bytes);
            }
            return (true, any ? newest : entry.LastWriteTimeUtc, bytes, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return (false, default, 0, ex.GetType().Name);
        }
    }
}
