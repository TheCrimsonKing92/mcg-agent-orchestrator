using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record PortfolioProject(
    string Id,
    string Title,
    string? ParentProjectId,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record PortfolioEpic(
    string Id,
    string Title,
    string? ProjectId,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record PortfolioMembership(
    string EpicId,
    string EpicTitle,
    string? ProjectId,
    string? ProjectTitle);

public sealed record PortfolioClusterSuggestion(
    string Id,
    string Signal,
    string Title,
    string Evidence,
    IReadOnlyList<string> GoalIds,
    IReadOnlyList<string> BacklogItemIds,
    DateTimeOffset CreatedAt);

public sealed record PortfolioEpicRollup(
    PortfolioEpic Epic,
    PortfolioProject? Project,
    int GoalCount,
    int BacklogItemCount,
    int ActiveCount,
    int VerifiedCount,
    int ParkedCount,
    int LandedCount,
    DateTimeOffset? NewestTransitionAt);

public sealed record PortfolioGoalRow(
    Goal Goal,
    PortfolioEpic Epic,
    PortfolioProject? Project,
    DateTimeOffset? NewestTransitionAt);

public sealed class PortfolioStore
{
    private const int MaxBusyRetries = 6;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dbPath;

    public PortfolioStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;";

    public async Task<PortfolioProject> AddProjectAsync(
        string title,
        string? parentProjectId = null,
        string actor = "operator",
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var project = new PortfolioProject(
            Guid.NewGuid().ToString("n"),
            RequireText(title, nameof(title)),
            NormalizeId(parentProjectId),
            now,
            RequireText(actor, nameof(actor)),
            now,
            RequireText(actor, nameof(actor)));

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await BeginImmediateAsync(conn, cancellationToken);
            try
            {
                if (project.ParentProjectId is not null)
                    await RequireProjectAsync(conn, project.ParentProjectId, cancellationToken);
                await InsertProjectAsync(conn, project, cancellationToken);
                await CommitAsync(conn, cancellationToken);
                return project;
            }
            catch
            {
                await RollbackQuietlyAsync(conn, cancellationToken);
                throw;
            }
        }, cancellationToken);
    }

    public async Task<PortfolioEpic> AddEpicAsync(
        string title,
        string? projectId = null,
        string actor = "operator",
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var epic = new PortfolioEpic(
            Guid.NewGuid().ToString("n"),
            RequireText(title, nameof(title)),
            NormalizeId(projectId),
            now,
            RequireText(actor, nameof(actor)),
            now,
            RequireText(actor, nameof(actor)));

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await BeginImmediateAsync(conn, cancellationToken);
            try
            {
                if (epic.ProjectId is not null)
                    await RequireProjectAsync(conn, epic.ProjectId, cancellationToken);
                await InsertEpicAsync(conn, epic, cancellationToken);
                await CommitAsync(conn, cancellationToken);
                return epic;
            }
            catch
            {
                await RollbackQuietlyAsync(conn, cancellationToken);
                throw;
            }
        }, cancellationToken);
    }

    public async Task AssignEpicToProjectAsync(
        string epicId,
        string projectId,
        string actor = "operator",
        CancellationToken cancellationToken = default)
    {
        await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await BeginImmediateAsync(conn, cancellationToken);
            try
            {
                await RequireEpicAsync(conn, epicId, cancellationToken);
                await RequireProjectAsync(conn, projectId, cancellationToken);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE epics SET project_id = $project_id, updated_at = $updated_at, updated_by = $updated_by WHERE id = $id";
                cmd.Parameters.AddWithValue("$project_id", projectId);
                cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$updated_by", RequireText(actor, nameof(actor)));
                cmd.Parameters.AddWithValue("$id", epicId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                await CommitAsync(conn, cancellationToken);
                return true;
            }
            catch
            {
                await RollbackQuietlyAsync(conn, cancellationToken);
                throw;
            }
        }, cancellationToken);
    }

    public Task AssignGoalToEpicAsync(string goalId, string epicId, string actor = "operator", CancellationToken cancellationToken = default) =>
        AssignMemberAsync("goal_epic_memberships", "goal_id", goalId, epicId, actor, cancellationToken);

    public Task AssignBacklogItemToEpicAsync(string backlogItemId, string epicId, string actor = "operator", CancellationToken cancellationToken = default) =>
        AssignMemberAsync("backlog_epic_memberships", "backlog_item_id", backlogItemId, epicId, actor, cancellationToken);

    public async Task<IReadOnlyList<PortfolioProject>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var projects = new List<PortfolioProject>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, parent_project_id, created_at, created_by, updated_at, updated_by FROM projects ORDER BY title COLLATE NOCASE, id";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            projects.Add(ReadProject(reader));
        return projects;
    }

    public async Task<IReadOnlyList<PortfolioEpic>> ListEpicsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var epics = new List<PortfolioEpic>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, project_id, created_at, created_by, updated_at, updated_by FROM epics ORDER BY title COLLATE NOCASE, id";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            epics.Add(ReadEpic(reader));
        return epics;
    }

    public async Task<PortfolioMembership?> GetGoalMembershipAsync(string goalId, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        return await GetMembershipAsync(conn, "goal_epic_memberships", "goal_id", goalId, cancellationToken);
    }

    public async Task<PortfolioMembership?> GetBacklogMembershipAsync(string backlogItemId, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        return await GetMembershipAsync(conn, "backlog_epic_memberships", "backlog_item_id", backlogItemId, cancellationToken);
    }

    public async Task<PortfolioEpic?> ResolveEpicAsync(string idOrTitle, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        return await ResolveEpicAsync(conn, idOrTitle, cancellationToken);
    }

    public async Task<PortfolioProject?> ResolveProjectAsync(string idOrTitle, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        return await ResolveProjectAsync(conn, idOrTitle, cancellationToken);
    }

    public async Task<IReadOnlyList<PortfolioEpicRollup>> BuildEpicRollupsAsync(
        IReadOnlyCollection<Goal> goals,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var epics = await LoadEpicsAsync(conn, cancellationToken);
        var projects = (await LoadProjectsAsync(conn, cancellationToken)).ToDictionary(project => project.Id, StringComparer.Ordinal);
        var goalMemberships = await LoadMembershipsAsync(conn, "goal_epic_memberships", "goal_id", cancellationToken);
        var backlogMemberships = await LoadMembershipsAsync(conn, "backlog_epic_memberships", "backlog_item_id", cancellationToken);
        var goalsById = goals.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);

        return epics
            .Select(epic =>
            {
                var epicGoals = goalMemberships
                    .Where(pair => pair.Value.Equals(epic.Id, StringComparison.Ordinal))
                    .Select(pair => goalsById.TryGetValue(pair.Key, out var goal) ? goal : null)
                    .Where(goal => goal is not null)
                    .Cast<Goal>()
                    .ToArray();
                var newest = epicGoals
                    .Select(NewestTransitionAt)
                    .Where(value => value is not null)
                    .Cast<DateTimeOffset>()
                    .DefaultIfEmpty()
                    .Max();
                return new PortfolioEpicRollup(
                    epic,
                    epic.ProjectId is not null && projects.TryGetValue(epic.ProjectId, out var project) ? project : null,
                    epicGoals.Length,
                    backlogMemberships.Count(pair => pair.Value.Equals(epic.Id, StringComparison.Ordinal)),
                    epicGoals.Count(goal => goal.Status is GoalStatus.Active or GoalStatus.WaitingForHuman or GoalStatus.Draft),
                    epicGoals.Count(goal => goal.Status == GoalStatus.Verified),
                    epicGoals.Count(goal => goal.Status == GoalStatus.Parked),
                    epicGoals.Count(goal => goal.Status == GoalStatus.Completed),
                    newest == default ? null : newest);
            })
            .OrderBy(row => row.Project?.Title ?? "~")
            .ThenBy(row => row.Epic.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<PortfolioGoalRow>> BuildPortfolioGoalRowsAsync(
        IReadOnlyCollection<Goal> goals,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var epics = (await LoadEpicsAsync(conn, cancellationToken)).ToDictionary(epic => epic.Id, StringComparer.Ordinal);
        var projects = (await LoadProjectsAsync(conn, cancellationToken)).ToDictionary(project => project.Id, StringComparer.Ordinal);
        var goalMemberships = await LoadMembershipsAsync(conn, "goal_epic_memberships", "goal_id", cancellationToken);

        return goals
            .Where(goal => goalMemberships.ContainsKey(goal.Id.Value))
            .Select(goal =>
            {
                var epic = epics[goalMemberships[goal.Id.Value]];
                var project = epic.ProjectId is not null && projects.TryGetValue(epic.ProjectId, out var resolvedProject)
                    ? resolvedProject
                    : null;
                return new PortfolioGoalRow(goal, epic, project, NewestTransitionAt(goal));
            })
            .OrderBy(row => row.Project?.Title ?? "~", StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Epic.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Goal.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<PortfolioClusterSuggestion>> ReplaceSuggestionsAsync(
        IReadOnlyList<PortfolioClusterSuggestion> suggestions,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await BeginImmediateAsync(conn, cancellationToken);
            try
            {
                await RunNonQueryAsync(conn, "DELETE FROM cluster_suggestions", cancellationToken);
                foreach (var suggestion in suggestions)
                    await InsertSuggestionAsync(conn, suggestion, cancellationToken);
                await CommitAsync(conn, cancellationToken);
                return suggestions;
            }
            catch
            {
                await RollbackQuietlyAsync(conn, cancellationToken);
                throw;
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<PortfolioClusterSuggestion>> ListSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var suggestions = new List<PortfolioClusterSuggestion>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, signal, title, evidence, goal_ids_json, backlog_item_ids_json, created_at
            FROM cluster_suggestions
            ORDER BY created_at DESC, signal, title COLLATE NOCASE
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            suggestions.Add(ReadSuggestion(reader));
        return suggestions;
    }

    private async Task AssignMemberAsync(
        string table,
        string idColumn,
        string memberId,
        string epicId,
        string actor,
        CancellationToken cancellationToken)
    {
        await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await BeginImmediateAsync(conn, cancellationToken);
            try
            {
                await RequireEpicAsync(conn, epicId, cancellationToken);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    INSERT INTO {table} ({idColumn}, epic_id, created_at, created_by)
                    VALUES ($member_id, $epic_id, $created_at, $created_by)
                    ON CONFLICT({idColumn}) DO UPDATE SET
                        epic_id = excluded.epic_id,
                        created_at = excluded.created_at,
                        created_by = excluded.created_by
                    """;
                cmd.Parameters.AddWithValue("$member_id", RequireText(memberId, nameof(memberId)));
                cmd.Parameters.AddWithValue("$epic_id", epicId);
                cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$created_by", RequireText(actor, nameof(actor)));
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                await CommitAsync(conn, cancellationToken);
                return true;
            }
            catch
            {
                await RollbackQuietlyAsync(conn, cancellationToken);
                throw;
            }
        }, cancellationToken);
    }

    private void EnsureSchema()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        RunNonQuery(conn, "PRAGMA journal_mode=WAL");
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        RunNonQuery(conn, "PRAGMA foreign_keys=ON");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS projects (
                id                TEXT PRIMARY KEY,
                title             TEXT NOT NULL,
                parent_project_id TEXT NULL,
                created_at        TEXT NOT NULL,
                created_by        TEXT NOT NULL,
                updated_at        TEXT NOT NULL,
                updated_by        TEXT NOT NULL,
                FOREIGN KEY(parent_project_id) REFERENCES projects(id) ON DELETE SET NULL
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_projects_title ON projects(title COLLATE NOCASE)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS epics (
                id         TEXT PRIMARY KEY,
                title      TEXT NOT NULL,
                project_id TEXT NULL,
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL,
                FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE SET NULL
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_epics_title ON epics(title COLLATE NOCASE)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_epics_project ON epics(project_id)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS goal_epic_memberships (
                goal_id    TEXT PRIMARY KEY,
                epic_id    TEXT NOT NULL,
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL,
                FOREIGN KEY(epic_id) REFERENCES epics(id) ON DELETE CASCADE
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_goal_epic_memberships_epic ON goal_epic_memberships(epic_id)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS backlog_epic_memberships (
                backlog_item_id TEXT PRIMARY KEY,
                epic_id         TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                created_by      TEXT NOT NULL,
                FOREIGN KEY(epic_id) REFERENCES epics(id) ON DELETE CASCADE
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_backlog_epic_memberships_epic ON backlog_epic_memberships(epic_id)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS cluster_suggestions (
                id                    TEXT PRIMARY KEY,
                signal                TEXT NOT NULL,
                title                 TEXT NOT NULL,
                evidence              TEXT NOT NULL,
                goal_ids_json         TEXT NOT NULL,
                backlog_item_ids_json TEXT NOT NULL,
                created_at            TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_cluster_suggestions_signal ON cluster_suggestions(signal, created_at)");
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        RunNonQuery(conn, "PRAGMA foreign_keys=ON");
        return conn;
    }

    private static async Task BeginImmediateAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
        await RunNonQueryAsync(conn, "PRAGMA foreign_keys=ON", cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
    }

    private static Task CommitAsync(SqliteConnection conn, CancellationToken cancellationToken) =>
        RunNonQueryAsync(conn, "COMMIT", cancellationToken);

    private static async Task RollbackQuietlyAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
    }

    private static async Task<PortfolioMembership?> GetMembershipAsync(
        SqliteConnection conn,
        string table,
        string idColumn,
        string id,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.id, e.title, p.id, p.title
            FROM {table} m
            JOIN epics e ON e.id = m.epic_id
            LEFT JOIN projects p ON p.id = e.project_id
            WHERE m.{idColumn} = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PortfolioMembership(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    private static async Task<Dictionary<string, string>> LoadMembershipsAsync(SqliteConnection conn, string table, string idColumn, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {idColumn}, epic_id FROM {table}";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results[reader.GetString(0)] = reader.GetString(1);
        return results;
    }

    private static async Task<IReadOnlyList<PortfolioProject>> LoadProjectsAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        var projects = new List<PortfolioProject>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, parent_project_id, created_at, created_by, updated_at, updated_by FROM projects";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            projects.Add(ReadProject(reader));
        return projects;
    }

    private static async Task<IReadOnlyList<PortfolioEpic>> LoadEpicsAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        var epics = new List<PortfolioEpic>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, project_id, created_at, created_by, updated_at, updated_by FROM epics";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            epics.Add(ReadEpic(reader));
        return epics;
    }

    private static async Task<PortfolioEpic?> ResolveEpicAsync(SqliteConnection conn, string idOrTitle, CancellationToken cancellationToken)
    {
        var matches = new List<PortfolioEpic>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, project_id, created_at, created_by, updated_at, updated_by
            FROM epics
            WHERE id LIKE $prefix OR title = $title COLLATE NOCASE
            ORDER BY CASE WHEN title = $title COLLATE NOCASE THEN 0 ELSE 1 END, title COLLATE NOCASE
            LIMIT 2
            """;
        cmd.Parameters.AddWithValue("$prefix", idOrTitle + "%");
        cmd.Parameters.AddWithValue("$title", idOrTitle);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            matches.Add(ReadEpic(reader));
        return ResolveSingle(matches, idOrTitle, "Epic");
    }

    private static async Task<PortfolioProject?> ResolveProjectAsync(SqliteConnection conn, string idOrTitle, CancellationToken cancellationToken)
    {
        var matches = new List<PortfolioProject>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, parent_project_id, created_at, created_by, updated_at, updated_by
            FROM projects
            WHERE id LIKE $prefix OR title = $title COLLATE NOCASE
            ORDER BY CASE WHEN title = $title COLLATE NOCASE THEN 0 ELSE 1 END, title COLLATE NOCASE
            LIMIT 2
            """;
        cmd.Parameters.AddWithValue("$prefix", idOrTitle + "%");
        cmd.Parameters.AddWithValue("$title", idOrTitle);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            matches.Add(ReadProject(reader));
        return ResolveSingle(matches, idOrTitle, "Project");
    }

    private static T? ResolveSingle<T>(IReadOnlyList<T> matches, string value, string label) =>
        matches.Count switch
        {
            0 => default,
            1 => matches[0],
            _ => throw new InvalidOperationException($"{label} '{value}' is ambiguous.")
        };

    private static async Task RequireProjectAsync(SqliteConnection conn, string projectId, CancellationToken cancellationToken)
    {
        if (await ResolveProjectAsync(conn, projectId, cancellationToken) is null)
            throw new InvalidOperationException($"Project '{projectId}' was not found.");
    }

    private static async Task RequireEpicAsync(SqliteConnection conn, string epicId, CancellationToken cancellationToken)
    {
        if (await ResolveEpicAsync(conn, epicId, cancellationToken) is null)
            throw new InvalidOperationException($"Epic '{epicId}' was not found.");
    }

    private static async Task InsertProjectAsync(SqliteConnection conn, PortfolioProject project, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, title, parent_project_id, created_at, created_by, updated_at, updated_by)
            VALUES ($id, $title, $parent_project_id, $created_at, $created_by, $updated_at, $updated_by)
            """;
        cmd.Parameters.AddWithValue("$id", project.Id);
        cmd.Parameters.AddWithValue("$title", project.Title);
        cmd.Parameters.AddWithValue("$parent_project_id", (object?)project.ParentProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", project.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$created_by", project.CreatedBy);
        cmd.Parameters.AddWithValue("$updated_at", project.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated_by", project.UpdatedBy);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEpicAsync(SqliteConnection conn, PortfolioEpic epic, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO epics (id, title, project_id, created_at, created_by, updated_at, updated_by)
            VALUES ($id, $title, $project_id, $created_at, $created_by, $updated_at, $updated_by)
            """;
        cmd.Parameters.AddWithValue("$id", epic.Id);
        cmd.Parameters.AddWithValue("$title", epic.Title);
        cmd.Parameters.AddWithValue("$project_id", (object?)epic.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", epic.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$created_by", epic.CreatedBy);
        cmd.Parameters.AddWithValue("$updated_at", epic.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated_by", epic.UpdatedBy);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSuggestionAsync(SqliteConnection conn, PortfolioClusterSuggestion suggestion, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cluster_suggestions (id, signal, title, evidence, goal_ids_json, backlog_item_ids_json, created_at)
            VALUES ($id, $signal, $title, $evidence, $goal_ids_json, $backlog_item_ids_json, $created_at)
            """;
        cmd.Parameters.AddWithValue("$id", suggestion.Id);
        cmd.Parameters.AddWithValue("$signal", suggestion.Signal);
        cmd.Parameters.AddWithValue("$title", suggestion.Title);
        cmd.Parameters.AddWithValue("$evidence", suggestion.Evidence);
        cmd.Parameters.AddWithValue("$goal_ids_json", JsonSerializer.Serialize(suggestion.GoalIds, JsonOptions));
        cmd.Parameters.AddWithValue("$backlog_item_ids_json", JsonSerializer.Serialize(suggestion.BacklogItemIds, JsonOptions));
        cmd.Parameters.AddWithValue("$created_at", suggestion.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PortfolioProject ReadProject(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)),
            reader.GetString(6));

    private static PortfolioEpic ReadEpic(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)),
            reader.GetString(6));

    private static PortfolioClusterSuggestion ReadSuggestion(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(4), JsonOptions) ?? [],
            JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(5), JsonOptions) ?? [],
            DateTimeOffset.Parse(reader.GetString(6)));

    private static DateTimeOffset? NewestTransitionAt(Goal goal) =>
        goal.Timeline.Count == 0 ? null : goal.Timeline.Max(evt => evt.OccurredAt);

    private static bool IsTransientLock(SqliteException ex) =>
        ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;

    private static async Task<T> WithBusyRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var delayMs = 50;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqliteException ex) when (attempt < MaxBusyRetries && IsTransientLock(ex))
            {
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, 1000);
            }
        }
    }

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task RunNonQueryAsync(SqliteConnection conn, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty.", parameterName);
        return value.Trim();
    }

    private static string? NormalizeId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
