using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BoardFillDraftRound(string Id, string BacklogItemId, DateTimeOffset ItemUpdatedAt,
    DateTimeOffset? ItemNewestNoteAt, string? MainHead, string? Outcome, string? DraftPath, string? ReceiptPath,
    IReadOnlyList<AuthorBriefDraftCheck> Checks, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string? Failure = null, bool Reported = false)
{
    internal DateTimeOffset ChangeStamp => ItemNewestNoteAt is { } note && note > ItemUpdatedAt ? note : ItemUpdatedAt;
}

// Only this service's rounds live here. Reads do not create a file or change a schema.
internal sealed class ConductorBoardFillDraftStore(string path)
{
    internal IReadOnlyList<BoardFillDraftRound> ReadAll()
    {
        if (!File.Exists(path)) return [];
        using var connection = Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM board_fill_rounds ORDER BY round_id";
        using var reader = command.ExecuteReader();
        var rounds = new List<BoardFillDraftRound>();
        while (reader.Read())
            rounds.Add(JsonSerializer.Deserialize<BoardFillDraftRound>(reader.GetString(0))
                ?? throw new InvalidDataException("Invalid board-fill round."));
        return rounds;
    }

    internal BoardFillDraftRound Begin(BacklogItem item, DateTimeOffset now)
    {
        var round = new BoardFillDraftRound(Guid.NewGuid().ToString("N"), item.Id, item.UpdatedAt,
            item.Notes.Count == 0 ? null : item.Notes.Max(note => note.CreatedAt),
            null, null, null, null, [], now, null);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO board_fill_rounds(round_id, payload) VALUES($id, $payload)";
        command.Parameters.AddWithValue("$id", round.Id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(round));
        command.ExecuteNonQuery();
        return round;
    }

    internal void Finish(BoardFillDraftRound round, AuthorBriefDraftOutcome outcome, DateTimeOffset now)
    {
        if (outcome.Kind is not ("draft" or "stale" or "failed"))
            throw new InvalidOperationException($"Invalid board-fill outcome '{outcome.Kind}'.");
        Update(round.Id, current => current.Outcome is null ? current with
        {
            MainHead = outcome.MainHead, Outcome = outcome.Kind, DraftPath = outcome.DraftPath,
            ReceiptPath = outcome.ReceiptPath, Checks = outcome.Checks, FinishedAt = now, Failure = outcome.Failure
        } : throw new InvalidOperationException("Board-fill round is already finished."));
    }

    internal void MarkReported(string id) => Update(id, current =>
        current.Outcome is not null ? current with { Reported = true } :
        throw new InvalidOperationException("Cannot report an unfinished board-fill round."));

    internal int StartedOnUtcDay(DateTimeOffset now) =>
        ReadAll().Count(round => round.StartedAt.UtcDateTime.Date == now.UtcDateTime.Date);

    internal IReadOnlySet<string> AlreadyDrafted(IReadOnlyList<BacklogItem> items)
    {
        var verdicts = ReadAll().Where(round => round.Outcome is "draft" or "stale")
            .GroupBy(round => round.BacklogItemId).ToDictionary(group => group.Key, group => group.Max(round => round.ChangeStamp));
        return items.Where(item => verdicts.TryGetValue(item.Id, out var stamp) &&
            stamp >= BoardFillReadyItemSelector.ChangeStamp(item)).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
    }

    private void Update(string id, Func<BoardFillDraftRound, BoardFillDraftRound> change)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM board_fill_rounds WHERE round_id=$id";
        command.Parameters.AddWithValue("$id", id);
        var current = JsonSerializer.Deserialize<BoardFillDraftRound>((string?)command.ExecuteScalar()
            ?? throw new InvalidOperationException("Board-fill round does not exist."))
            ?? throw new InvalidDataException("Invalid board-fill round.");
        command.CommandText = "UPDATE board_fill_rounds SET payload=$payload WHERE round_id=$id";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(change(current)));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection Open(bool readOnly = false)
    {
        if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            if (!readOnly)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE IF NOT EXISTS board_fill_rounds(round_id TEXT PRIMARY KEY, payload TEXT NOT NULL)";
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
