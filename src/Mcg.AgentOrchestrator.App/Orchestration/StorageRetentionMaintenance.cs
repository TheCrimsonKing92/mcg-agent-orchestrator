using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record StorageRetentionGoal(
    string GoalId,
    GoalStatus Status,
    IReadOnlyDictionary<string, WorkTaskStatus> TaskStatuses)
{
    public bool IsTerminal => Status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

    public static StorageRetentionGoal FromSnapshot(GoalSnapshot snapshot) =>
        new(
            snapshot.Id,
            snapshot.Status,
            snapshot.Tasks.ToDictionary(task => task.Id, task => task.Status, StringComparer.OrdinalIgnoreCase));
}

internal sealed record StorageRetentionResult(
    int WorkerArtifactsDeleted,
    int WorkerLogsCompressed,
    int SuccessfulTrxReceiptsWritten,
    int AcceptanceArtifactsDeleted,
    int GoalJournalsArchived);

internal static partial class StorageRetentionMaintenance
{
    internal static readonly TimeSpan WorkerCompressionAge = TimeSpan.FromDays(1);
    internal static readonly TimeSpan WorkerDeletionAge = TimeSpan.FromDays(14);
    internal static readonly TimeSpan AcceptanceArtifactMaxAge = TimeSpan.FromDays(14);
    internal const long AcceptanceArtifactMaxBytes = 256L * 1024 * 1024;

    [GeneratedRegex("^(?<dispatch>(?<goal>[0-9a-f]{8})-(?<task>[0-9a-f]{8})-[0-9]{14})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DispatchArtifactNameRegex();

    public static StorageRetentionResult Run(
        OrchestratorWorkspace workspace,
        IEnumerable<Goal> goals,
        DateTimeOffset now)
    {
        var retentionGoals = goals.Select(goal => new StorageRetentionGoal(
            goal.Id.Value,
            goal.Status,
            goal.Tasks.ToDictionary(task => task.Id.Value, task => task.Status, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        return Run(workspace.LogDirectory, workspace.OrchestratorDirectory, workspace.ExecutionDirectory, retentionGoals, now);
    }

    internal static IReadOnlyCollection<StorageRetentionGoal> LoadPersistedTerminalGoals(
        ITransactionalOrchestratorStateRepository stateRepository)
    {
        var terminalGoalIds = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(summary =>
                Enum.TryParse<GoalStatus>(summary.Status, ignoreCase: true, out var status) &&
                status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded)
            .Select(summary => new GoalId(summary.Id))
            .ToArray();
        var goals = new List<StorageRetentionGoal>(terminalGoalIds.Length);
        foreach (var goalId in terminalGoalIds)
        {
            var snapshot = stateRepository.LoadGoalAsync(goalId).GetAwaiter().GetResult();
            if (snapshot is not null)
            {
                goals.Add(StorageRetentionGoal.FromSnapshot(snapshot));
            }
        }

        return goals;
    }

    internal static StorageRetentionResult Run(
        string logDirectory,
        string orchestratorDirectory,
        string executionDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now)
    {
        var worker = SweepWorkerArtifacts(logDirectory, goals, now);
        var acceptance = SweepAcceptanceArtifacts(orchestratorDirectory, goals, now);
        var journals = ArchiveGoalJournals(executionDirectory, goals);
        return new StorageRetentionResult(
            worker.Deleted,
            worker.Compressed,
            acceptance.Receipts,
            acceptance.Deleted,
            journals);
    }

    private static (int Deleted, int Compressed) SweepWorkerArtifacts(
        string logDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now)
    {
        if (!Directory.Exists(logDirectory))
        {
            return (0, 0);
        }

        var byPrefix = goals
            .GroupBy(goal => goal.GoalId[..Math.Min(8, goal.GoalId.Length)], StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var artifacts = Directory.EnumerateFiles(logDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Match: DispatchArtifactNameRegex().Match(Path.GetFileName(path))))
            .ToArray();
        var dispatchFailures = artifacts
            .Where(artifact =>
                artifact.Match.Success &&
                byPrefix.TryGetValue(artifact.Match.Groups["goal"].Value, out var goal) &&
                goal.IsTerminal)
            .Select(artifact => artifact.Match.Groups["dispatch"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                dispatchPrefix => dispatchPrefix,
                dispatchPrefix => DispatchFailed(logDirectory, dispatchPrefix),
                StringComparer.OrdinalIgnoreCase);
        var deleted = TryDeleteExclusive(Path.Combine(logDirectory, "dispatch-diagnostics.jsonl")) ? 1 : 0;
        var compressed = 0;
        foreach (var artifact in artifacts)
        {
            var (path, match) = artifact;
            if (!match.Success || !byPrefix.TryGetValue(match.Groups["goal"].Value, out var goal) || !goal.IsTerminal)
            {
                continue;
            }

            var taskPrefix = match.Groups["task"].Value;
            var matchingTasks = goal.TaskStatuses
                .Where(pair => pair.Key.StartsWith(taskPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
                .ToArray();
            var preserveRaw = goal.Status == GoalStatus.Failed && matchingTasks.Length == 0 ||
                matchingTasks.Any(status => status is WorkTaskStatus.Failed or WorkTaskStatus.WaitingForHuman) ||
                dispatchFailures[match.Groups["dispatch"].Value];
            if (preserveRaw)
            {
                continue;
            }

            var age = now - File.GetLastWriteTimeUtc(path);
            if (age > WorkerDeletionAge)
            {
                if (TryDeleteExclusive(path))
                {
                    deleted++;
                }
                continue;
            }

            if (age >= WorkerCompressionAge && IsRawWorkerLog(path) && TryCompressExclusive(path))
            {
                compressed++;
            }
        }

        return (deleted, compressed);
    }

    private static bool DispatchFailed(string logDirectory, string dispatchPrefix)
    {
        var exitPath = Path.Combine(logDirectory, dispatchPrefix + ".exit.txt");
        if (DispatchExitArtifacts.TryRead(exitPath, out var exitArtifact))
        {
            return exitArtifact.ExitCode != 0;
        }

        var childExitPath = Path.Combine(logDirectory, dispatchPrefix + ".child-exit.json");
        try
        {
            if (!File.Exists(childExitPath))
            {
                // A terminal task without any durable exit artifact is the killed/hung/reaped case.
                // Preserve its raw logs because successful completion has not been positively established.
                return true;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(childExitPath));
            return document.RootElement.TryGetProperty("exitCode", out var exitCode) &&
                exitCode.ValueKind == JsonValueKind.Number &&
                exitCode.GetInt32() != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    private static bool IsRawWorkerLog(string path) =>
        !path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) &&
        (path.EndsWith(".out.log", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".err.log", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Contains(".log.part-", StringComparison.OrdinalIgnoreCase));

    private static bool TryCompressExclusive(string path)
    {
        var gzipPath = path + ".gz";
        var temporaryPath = gzipPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            {
                input.CopyTo(gzip);
            }

            File.Move(temporaryPath, gzipPath, overwrite: true);
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            return false;
        }
    }

    private static (int Receipts, int Deleted) SweepAcceptanceArtifacts(
        string orchestratorDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now)
    {
        var root = Path.Combine(orchestratorDirectory, "acceptance-gate-attempts");
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        var terminal = goals.Where(goal => goal.IsTerminal)
            .ToDictionary(goal => goal.GoalId, StringComparer.OrdinalIgnoreCase);
        var receipts = 0;
        var deleted = 0;
        var candidates = new List<FileInfo>();
        foreach (var goalDirectory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var goalId = Path.GetFileName(goalDirectory);
            if (!terminal.ContainsKey(goalId))
            {
                continue;
            }

            var lastFailingAttempt = FindLastFailingAttempt(goalDirectory);
            foreach (var trxPath in Directory.EnumerateFiles(goalDirectory, "*.trx", SearchOption.AllDirectories))
            {
                if (BelongsToAttempt(trxPath, lastFailingAttempt) || !TryWriteSuccessfulTrxReceipt(trxPath))
                {
                    continue;
                }

                receipts++;
                if (TryDeleteExclusive(trxPath))
                {
                    deleted++;
                }
            }

            foreach (var path in Directory.EnumerateFiles(goalDirectory, "*", SearchOption.AllDirectories))
            {
                if (!IsAcceptanceSummary(path) && !BelongsToAttempt(path, lastFailingAttempt))
                {
                    candidates.Add(new FileInfo(path));
                }
            }
        }

        var totalBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(SafeLength)
            .Sum();
        foreach (var candidate in candidates.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
        {
            var aged = now - candidate.LastWriteTimeUtc > AcceptanceArtifactMaxAge;
            if (!aged && totalBytes <= AcceptanceArtifactMaxBytes)
            {
                continue;
            }

            var length = SafeLength(candidate.FullName);
            if (TryDeleteExclusive(candidate.FullName))
            {
                deleted++;
                totalBytes = Math.Max(0, totalBytes - length);
            }
        }

        return (receipts, deleted);
    }

    private static string? FindLastFailingAttempt(string goalDirectory)
    {
        return Directory.EnumerateFiles(goalDirectory, "*.attempt.json", SearchOption.TopDirectoryOnly)
            .Select(path => new { Path = path, Outcome = TryReadAttemptOutcome(path), At = File.GetLastWriteTimeUtc(path) })
            .Where(item => item.Outcome == "failed")
            .OrderByDescending(item => item.At)
            .Select(item => Path.GetFileName(item.Path)[..^".attempt.json".Length])
            .FirstOrDefault();
    }

    private static string? TryReadAttemptOutcome(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("outcome", out var outcome))
            {
                return null;
            }

            if (outcome.ValueKind == JsonValueKind.String)
            {
                return outcome.GetString()?.ToLowerInvariant();
            }

            return outcome.ValueKind == JsonValueKind.Number && outcome.TryGetInt32(out var numeric) && numeric == 2
                ? "failed"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool TryWriteSuccessfulTrxReceipt(string trxPath)
    {
        var receiptPath = trxPath + ".test-identities.json";
        if (File.Exists(receiptPath))
        {
            try
            {
                using var existing = JsonDocument.Parse(File.ReadAllText(receiptPath));
                return existing.RootElement.TryGetProperty("testIdentities", out var identities) &&
                    identities.ValueKind == JsonValueKind.Array && identities.GetArrayLength() > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }

        var temporaryPath = receiptPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var input = new FileStream(trxPath, FileMode.Open, FileAccess.Read, FileShare.None);
            var document = XDocument.Load(input, LoadOptions.None);
            var counters = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Counters");
            if (counters is null ||
                !long.TryParse(counters.Attribute("failed")?.Value, out var failed) ||
                failed != 0)
            {
                return false;
            }

            var identities = document.Descendants()
                .Where(element => element.Name.LocalName == "UnitTestResult")
                .Select(element => new
                {
                    name = element.Attribute("testName")?.Value,
                    outcome = element.Attribute("outcome")?.Value
                })
                .Where(identity => !string.IsNullOrWhiteSpace(identity.name))
                .Distinct()
                .OrderBy(identity => identity.name, StringComparer.Ordinal)
                .ToArray();
            if (identities.Length == 0)
            {
                return false;
            }
            var receipt = new
            {
                source = Path.GetFileName(trxPath),
                total = counters.Attribute("total")?.Value,
                executed = counters.Attribute("executed")?.Value,
                passed = counters.Attribute("passed")?.Value,
                failed = counters.Attribute("failed")?.Value,
                testIdentities = identities
            };
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(receipt));
            File.Move(temporaryPath, receiptPath, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or JsonException)
        {
            TryDelete(temporaryPath);
            return false;
        }
    }

    private static bool IsAcceptanceSummary(string path) =>
        path.EndsWith(".attempt.json", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".test-identities.json", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("attempt-sequence.txt", StringComparison.OrdinalIgnoreCase);

    private static bool BelongsToAttempt(string path, string? attemptId)
    {
        if (string.IsNullOrWhiteSpace(attemptId))
        {
            return false;
        }

        return Path.GetFileName(path).StartsWith(attemptId + ".", StringComparison.OrdinalIgnoreCase) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals(attemptId + ".receipts", StringComparison.OrdinalIgnoreCase));
    }

    private static int ArchiveGoalJournals(
        string executionDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals)
    {
        var archived = 0;
        foreach (var goal in goals.Where(goal => goal.IsTerminal))
        {
            var source = GoalOperationJournal.PathFor(executionDirectory, new GoalId(goal.GoalId));
            if (!File.Exists(source))
            {
                continue;
            }

            var journal = GoalOperationJournal.Read(executionDirectory, new GoalId(goal.GoalId));
            if (!GoalOperationJournal.HasRetiredTerminalDisposition(journal))
            {
                continue;
            }

            var destination = GoalOperationJournal.ArchivePathFor(executionDirectory, new GoalId(goal.GoalId));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    destination = Path.Combine(
                        Path.GetDirectoryName(destination)!,
                        $"{goal.GoalId}-{Guid.NewGuid():N}.jsonl");
                }
                File.Move(source, destination);
                archived++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Active readers/writers keep the journal in place; the next daily sweep retries it.
            }
        }

        return archived;
    }

    private static bool TryDeleteExclusive(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
            }
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
