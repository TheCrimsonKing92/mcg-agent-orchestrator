using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

var exitCode = await OrchestratorSqliteTools.RunAsync(args);
return exitCode;

internal static class OrchestratorSqliteTools
{
    private const string RepoRootEnvironmentVariable = "MCG_ORCHESTRATOR_REPOSITORY_ROOT";

    private static readonly HashSet<string> AllowedGoalStatuses = new(StringComparer.Ordinal)
    {
        "Proposed",
        "Active",
        "Blocked",
        "Completed",
        "Cancelled",
        "Failed"
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        return args[0] switch
        {
            "list-goals" => await ListGoalsAsync(args[1..]),
            "set-goal-status" => await SetGoalStatusAsync(args[1..]),
            "requeue-task" => await RequeueTaskAsync(args[1..]),
            _ => Fail($"Unknown command: {args[0]}")
        };
    }

    private static async Task<int> ListGoalsAsync(string[] args)
    {
        string? repoRoot = null;
        string? dbPath = null;
        string? status = null;
        var limit = 20;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--repo-root":
                    repoRoot = RequireValue(args, ref i, arg);
                    break;
                case "--db":
                case "--db-path":
                    dbPath = RequireValue(args, ref i, arg);
                    break;
                case "--status":
                    status = NormalizeStatus(RequireValue(args, ref i, arg));
                    if (!AllowedGoalStatuses.Contains(status))
                        return Fail($"Unsupported goal status '{status}'. Expected one of: {string.Join(", ", AllowedGoalStatuses)}.");
                    break;
                case "--limit":
                    if (!int.TryParse(RequireValue(args, ref i, arg), out limit) || limit < 1)
                        return Fail("--limit must be a positive integer.");
                    break;
                case "--help":
                case "-h":
                    PrintListGoalsUsage();
                    return 0;
                default:
                    return Fail($"Unknown option: {arg}");
            }
        }

        dbPath = ResolveStateDbPath(dbPath, repoRoot);

        await using var conn = CreateConnection(dbPath, readOnly: false);
        try
        {
            await conn.OpenAsync();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000");
            await RunNonQueryAsync(conn, "PRAGMA temp_store=MEMORY");
            await RunNonQueryAsync(conn, "PRAGMA query_only=ON");
        }
        catch (SqliteException ex)
        {
            return FailOpen(dbPath, ex);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = status is null
            ? """
              SELECT id, status, snapshot_json
              FROM goals
              ORDER BY updated_at DESC, id
              LIMIT $limit
              """
            : """
              SELECT id, status, snapshot_json
              FROM goals
              WHERE status = $status
              ORDER BY updated_at DESC, id
              LIMIT $limit
              """;
        cmd.Parameters.AddWithValue("$limit", limit);
        if (status is not null)
            cmd.Parameters.AddWithValue("$status", status);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                var rowStatus = reader.GetString(1);
                var snapshotJson = reader.GetString(2);
                Console.WriteLine($"{Short(id)} [{rowStatus}] {ExtractObjective(snapshotJson)}");
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 14)
        {
            return FailOpen(dbPath, ex);
        }

        return 0;
    }

    private static async Task<int> SetGoalStatusAsync(string[] args)
    {
        string? repoRoot = null;
        string? dbPath = null;
        string? status = null;
        var dryRun = false;
        var prefixes = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--repo-root":
                    repoRoot = RequireValue(args, ref i, arg);
                    break;
                case "--db":
                case "--db-path":
                    dbPath = RequireValue(args, ref i, arg);
                    break;
                case "--status":
                    status = RequireValue(args, ref i, arg);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--help":
                case "-h":
                    PrintSetGoalStatusUsage();
                    return 0;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                        return Fail($"Unknown option: {arg}");
                    prefixes.Add(arg);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(status))
            return Fail("Missing required --status <Status>.");
        status = NormalizeStatus(status);
        if (!AllowedGoalStatuses.Contains(status))
            return Fail($"Unsupported goal status '{status}'. Expected one of: {string.Join(", ", AllowedGoalStatuses)}.");

        if (prefixes.Count == 0)
            return Fail("Provide at least one goal id or unique prefix.");

        dbPath = ResolveStateDbPath(dbPath, repoRoot);

        await using var conn = CreateConnection(dbPath, readOnly: false);
        try
        {
            await conn.OpenAsync();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000");
        }
        catch (SqliteException ex)
        {
            return FailOpen(dbPath, ex);
        }

        var rows = await ResolveGoalsAsync(conn, prefixes);
        if (rows.Count == 0)
            return 0;

        if (dryRun)
        {
            foreach (var row in rows)
                Console.WriteLine($"DRY-RUN {Short(row.Id)}: {row.Status} -> {status}");
            return 0;
        }

        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE");
        try
        {
            foreach (var row in rows)
            {
                var snapshot = JsonNode.Parse(row.SnapshotJson)
                    ?? throw new InvalidOperationException($"Goal {row.Id} has invalid snapshot JSON.");
                snapshot["Status"] = status;

                var updatedJson = snapshot.ToJsonString();
                var updatedAt = DateTimeOffset.UtcNow.ToString("O");

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE goals
                    SET status = $status,
                        snapshot_json = $snapshot_json,
                        updated_at = $updated_at,
                        version = COALESCE(version, 0) + 1
                    WHERE id = $id
                    """;
                cmd.Parameters.AddWithValue("$id", row.Id);
                cmd.Parameters.AddWithValue("$status", status);
                cmd.Parameters.AddWithValue("$snapshot_json", updatedJson);
                cmd.Parameters.AddWithValue("$updated_at", updatedAt);
                await cmd.ExecuteNonQueryAsync();

                Console.WriteLine($"UPDATED {Short(row.Id)}: {row.Status} -> {status}");
            }

            await RunNonQueryAsync(conn, "COMMIT");
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK"); } catch { }
            throw;
        }

        return 0;
    }

    private static async Task<int> RequeueTaskAsync(string[] args)
    {
        string? repoRoot = null;
        string? dbPath = null;
        string? goalPrefix = null;
        string? note = null;
        int? taskNumber = null;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--repo-root":
                    repoRoot = RequireValue(args, ref i, arg);
                    break;
                case "--db":
                case "--db-path":
                    dbPath = RequireValue(args, ref i, arg);
                    break;
                case "--task-number":
                    if (!int.TryParse(RequireValue(args, ref i, arg), out var parsedTaskNumber) || parsedTaskNumber < 1)
                        return Fail("--task-number must be a positive integer.");
                    taskNumber = parsedTaskNumber;
                    break;
                case "--note":
                    note = RequireValue(args, ref i, arg);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--help":
                case "-h":
                    PrintRequeueTaskUsage();
                    return 0;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                        return Fail($"Unknown option: {arg}");
                    if (goalPrefix is not null)
                        return Fail("Provide exactly one goal prefix.");
                    goalPrefix = arg;
                    break;
            }
        }

        if (goalPrefix is null)
            return Fail("Missing goal prefix.");
        if (taskNumber is null)
            return Fail("Missing required --task-number <n>.");
        if (string.IsNullOrWhiteSpace(note))
            return Fail("Missing required --note <text>.");

        dbPath = ResolveStateDbPath(dbPath, repoRoot);

        await using var conn = CreateConnection(dbPath, readOnly: false);
        try
        {
            await conn.OpenAsync();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000");
        }
        catch (SqliteException ex)
        {
            return FailOpen(dbPath, ex);
        }

        var rows = await ResolveGoalsAsync(conn, [goalPrefix]);
        if (rows.Count == 0)
            return 0;

        var row = rows[0];
        var snapshot = JsonNode.Parse(row.SnapshotJson) as JsonObject
            ?? throw new InvalidOperationException($"Goal {row.Id} has invalid snapshot JSON.");
        var tasks = snapshot["Tasks"] as JsonArray
            ?? throw new InvalidOperationException($"Goal {row.Id} snapshot has no Tasks array.");
        if (taskNumber.Value > tasks.Count)
            return Fail($"Task number {taskNumber.Value} is out of range; goal has {tasks.Count} task(s).");

        var task = tasks[taskNumber.Value - 1] as JsonObject
            ?? throw new InvalidOperationException($"Task {taskNumber.Value} snapshot is not an object.");
        var taskId = task["Id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Task {taskNumber.Value} has no Id.");
        var previousStatus = task["Status"]?.GetValue<string>() ?? "<unknown>";
        var nextStatus = string.IsNullOrWhiteSpace(task["AssignedAgentId"]?.GetValue<string>())
            ? "Pending"
            : "Assigned";

        if (dryRun)
        {
            Console.WriteLine($"DRY-RUN {Short(row.Id)} task {taskNumber.Value}: {previousStatus} -> {nextStatus}; clear LastExecution/LastVerification/LastDispatch/LastProcess.");
            return 0;
        }

        task["Status"] = nextStatus;
        task["LastExecution"] = null;
        task["LastVerification"] = null;
        task["LastDispatch"] = null;
        task["LastProcess"] = null;
        task["SubscriptionRetryAfter"] = null;
        snapshot["Status"] = "Active";

        var timeline = snapshot["Timeline"] as JsonArray
            ?? throw new InvalidOperationException($"Goal {row.Id} snapshot has no Timeline array.");
        var occurredAt = DateTimeOffset.UtcNow.ToString("O");
        timeline.Add(new JsonObject
        {
            ["GoalId"] = row.Id,
            ["TaskId"] = taskId,
            ["Kind"] = "TaskRetried",
            ["Message"] = note.Trim(),
            ["OccurredAt"] = occurredAt
        });

        var updatedJson = snapshot.ToJsonString();
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE");
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE goals
                SET status = 'Active',
                    snapshot_json = $snapshot_json,
                    updated_at = $updated_at,
                    version = COALESCE(version, 0) + 1
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", row.Id);
            cmd.Parameters.AddWithValue("$snapshot_json", updatedJson);
            cmd.Parameters.AddWithValue("$updated_at", occurredAt);
            await cmd.ExecuteNonQueryAsync();

            await RunNonQueryAsync(conn, "COMMIT");
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK"); } catch { }
            throw;
        }

        Console.WriteLine($"REQUEUED {Short(row.Id)} task {taskNumber.Value}: {previousStatus} -> {nextStatus}");
        return 0;
    }

    private static async Task<List<GoalRow>> ResolveGoalsAsync(SqliteConnection conn, List<string> prefixes)
    {
        var rows = new List<GoalRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPrefix in prefixes)
        {
            var prefix = rawPrefix.Trim();
            if (prefix.Length == 0)
                throw new ArgumentException("Goal prefix cannot be empty.");

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, status, snapshot_json
                FROM goals
                WHERE id LIKE $prefix
                ORDER BY id
                """;
            cmd.Parameters.AddWithValue("$prefix", prefix + "%");

            var matches = new List<GoalRow>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                matches.Add(new GoalRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }

            if (matches.Count == 0)
                throw new InvalidOperationException($"No goal matches prefix '{prefix}'.");
            if (matches.Count > 1)
                throw new InvalidOperationException(
                    $"Goal prefix '{prefix}' is ambiguous: {string.Join(", ", matches.Select(match => Short(match.Id)))}");

            var match = matches[0];
            if (seen.Add(match.Id))
                rows.Add(match);
        }

        return rows;
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {option}.");
        index++;
        return args[index];
    }

    private static string NormalizeStatus(string status)
    {
        status = status.Trim();
        if (status.Length == 0)
            return status;

        foreach (var allowed in AllowedGoalStatuses)
        {
            if (string.Equals(allowed, status, StringComparison.OrdinalIgnoreCase))
                return allowed;
        }

        return status;
    }

    private static async Task RunNonQueryAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string ResolveStateDbPath(string? dbPath, string? repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(dbPath))
            return Path.GetFullPath(dbPath);

        return Path.Combine(ResolveRepoRoot(repoRoot), ".orchestrator", "state.db");
    }

    private static string ResolveRepoRoot(string? repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(repoRoot))
            return Path.GetFullPath(repoRoot);

        var configuredRoot = Environment.GetEnvironmentVariable(RepoRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return NormalizeStateRoot(Path.GetFullPath(configuredRoot));

        foreach (var candidate in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var resolved = TryFindRepoRoot(candidate);
            if (resolved is not null)
                return NormalizeStateRoot(resolved);
        }

        return NormalizeStateRoot(Path.GetFullPath(Environment.CurrentDirectory));
    }

    private static string? TryFindRepoRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var directory = new DirectoryInfo(Path.GetFullPath(candidate));
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static string NormalizeStateRoot(string repoRoot)
    {
        var primaryRoot = TryResolvePrimaryRootFromLinkedWorktree(repoRoot);
        if (primaryRoot is not null)
            return primaryRoot;

        if (File.Exists(Path.Combine(repoRoot, ".orchestrator", "state.db")))
            return repoRoot;

        return repoRoot;
    }

    private static string? TryResolvePrimaryRootFromLinkedWorktree(string repoRoot)
    {
        var gitFilePath = Path.Combine(repoRoot, ".git");
        if (!File.Exists(gitFilePath))
            return null;

        var gitFile = File.ReadAllText(gitFilePath).Trim();
        const string prefix = "gitdir:";
        if (!gitFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var gitDir = gitFile[prefix.Length..].Trim();
        if (!Path.IsPathRooted(gitDir))
            gitDir = Path.Combine(repoRoot, gitDir);

        gitDir = Path.GetFullPath(gitDir);
        var worktreesDirectory = Path.GetDirectoryName(gitDir);
        var commonGitDirectory = worktreesDirectory is null ? null : Path.GetDirectoryName(worktreesDirectory);
        var primaryRoot = commonGitDirectory is null ? null : Path.GetDirectoryName(commonGitDirectory);
        return string.IsNullOrWhiteSpace(primaryRoot) ? null : primaryRoot;
    }

    private static SqliteConnection CreateConnection(string dbPath, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        return new SqliteConnection(builder.ToString());
    }

    private static bool IsHelp(string arg) => arg is "--help" or "-h" or "help";

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        PrintUsage();
        return 1;
    }

    private static int FailOpen(string dbPath, SqliteException exception)
    {
        var message = exception.Message;
        var prefix = $"SQLite Error {exception.SqliteErrorCode}: ";
        if (message.StartsWith(prefix, StringComparison.Ordinal))
            message = message[prefix.Length..];

        Console.Error.WriteLine($"sqlite-tool: cannot open database at {dbPath} (SQLite Error {exception.SqliteErrorCode}: {message})");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  list-goals [--repo-root <path>] [--db <path>] [--status <Status>] [--limit <n>]");
        Console.WriteLine("  set-goal-status [--repo-root <path>] [--db <path>] [--dry-run] --status <Status> <goal-prefix>...");
        Console.WriteLine("  requeue-task [--repo-root <path>] [--db <path>] [--dry-run] --task-number <n> --note <text> <goal-prefix>");
    }

    private static void PrintListGoalsUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  list-goals [--repo-root <path>] [--db <path>] [--status <Status>] [--limit <n>]");
        Console.WriteLine();
        Console.WriteLine("Statuses:");
        Console.WriteLine($"  {string.Join(", ", AllowedGoalStatuses)}");
    }

    private static void PrintRequeueTaskUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  requeue-task [--repo-root <path>] [--db <path>] [--dry-run] --task-number <n> --note <text> <goal-prefix>");
    }

    private static void PrintSetGoalStatusUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  set-goal-status [--repo-root <path>] [--db <path>] [--dry-run] --status <Status> <goal-prefix>...");
        Console.WriteLine();
        Console.WriteLine("Statuses:");
        Console.WriteLine($"  {string.Join(", ", AllowedGoalStatuses)}");
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private static string ExtractObjective(string snapshotJson)
    {
        try
        {
            var snapshot = JsonNode.Parse(snapshotJson);
            var objective = snapshot?["Objective"]?.GetValue<string>()
                ?? snapshot?["Title"]?.GetValue<string>()
                ?? "";
            objective = objective.ReplaceLineEndings(" ").Trim();
            if (objective.Length > 140)
                objective = objective[..137] + "...";
            return objective;
        }
        catch
        {
            return "<invalid snapshot>";
        }
    }

    private sealed record GoalRow(string Id, string Status, string SnapshotJson);
}
