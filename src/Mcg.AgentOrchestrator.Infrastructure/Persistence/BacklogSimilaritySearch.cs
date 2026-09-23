using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record SimilarityDocument(
    string Kind,
    string Id,
    string Status,
    string Title,
    DateTimeOffset UpdatedAt,
    string Text);

public sealed record SimilarityHit(
    string Kind,
    string Id,
    string Status,
    string Title,
    DateTimeOffset UpdatedAt,
    double Rank,
    string? Excerpt);

public static class BacklogSimilaritySearch
{
    private static readonly AsyncLocal<string?> ForcedFailure = new();

    internal static string? ForcedFailureMessage
    {
        get => ForcedFailure.Value;
        set => ForcedFailure.Value = value;
    }

    public static async Task<IReadOnlyList<SimilarityDocument>> LoadBacklogDocumentsAsync(
        string backlogDbPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backlogDbPath))
            return [];

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backlogDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var items = new List<LoadedBacklogItem>();
        var itemIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, title, body, status, updated_at FROM backlog ORDER BY created_at, id";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var item = new LoadedBacklogItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    []);
                itemIndexes.Add(item.Id, items.Count);
                items.Add(item);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT backlog_item_id, text FROM backlog_notes ORDER BY created_at, id";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (itemIndexes.TryGetValue(reader.GetString(0), out var itemIndex))
                    items[itemIndex].Notes.Add(reader.GetString(1));
            }
        }

        return items.Select(item => new SimilarityDocument(
            "backlog",
            item.Id,
            item.Status,
            item.Title,
            item.UpdatedAt,
            string.Join('\n', new[] { item.Title, item.Body }.Concat(item.Notes)))).ToArray();
    }

    public static IReadOnlyList<SimilarityHit> Search(
        IReadOnlyList<SimilarityDocument> corpus,
        string queryText,
        int limit = 10,
        bool includeExcerpt = false,
        string? statusFilter = null,
        string? excludeId = null)
    {
        if (ForcedFailureMessage is { } forcedFailure)
            throw new InvalidOperationException(forcedFailure);

        if (limit <= 0)
            return [];

        var matchExpression = BuildMatchExpression(queryText);
        if (matchExpression.Length == 0)
            return [];

        var searchable = corpus
            .Where(document => !string.Equals(document.Id, excludeId, StringComparison.Ordinal))
            .Where(document =>
                !document.Kind.Equals("backlog", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(statusFilter) ||
                document.Status.Equals(statusFilter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (searchable.Length == 0)
            return [];

        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False;");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE docs USING fts5(text, tokenize='unicode61')";
            create.ExecuteNonQuery();
        }

        using (var transaction = connection.BeginTransaction())
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO docs(rowid, text) VALUES ($rowid, $text)";
            var rowId = insert.Parameters.Add("$rowid", SqliteType.Integer);
            var text = insert.Parameters.Add("$text", SqliteType.Text);
            for (var index = 0; index < searchable.Length; index++)
            {
                rowId.Value = index + 1;
                text.Value = searchable[index].Text;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        using var search = connection.CreateCommand();
        search.CommandText = includeExcerpt
            ? "SELECT rowid, bm25(docs), snippet(docs, 0, '', '', '…', 32) FROM docs WHERE docs MATCH $match ORDER BY bm25(docs), rowid LIMIT $limit"
            : "SELECT rowid, bm25(docs), NULL FROM docs WHERE docs MATCH $match ORDER BY bm25(docs), rowid LIMIT $limit";
        search.Parameters.AddWithValue("$match", matchExpression);
        search.Parameters.AddWithValue("$limit", limit);

        var hits = new List<SimilarityHit>();
        using var reader = search.ExecuteReader();
        while (reader.Read())
        {
            var document = searchable[checked((int)reader.GetInt64(0) - 1)];
            hits.Add(new SimilarityHit(
                document.Kind,
                document.Id,
                document.Status,
                document.Title,
                document.UpdatedAt,
                reader.GetDouble(1),
                includeExcerpt ? NormalizeExcerpt(reader.GetString(2)) : null));
        }

        return hits;
    }

    internal static string BuildMatchExpression(string queryText)
    {
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var word = new StringBuilder();

        void FlushWord()
        {
            if (word.Length == 0)
                return;

            var value = word.ToString();
            if (seen.Add(value))
                words.Add($"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
            word.Clear();
        }

        foreach (var character in queryText ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character))
                word.Append(character);
            else
                FlushWord();
        }
        FlushWord();
        return string.Join(" OR ", words);
    }

    private static string NormalizeExcerpt(string excerpt)
    {
        var normalized = string.Join(' ', excerpt
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }

    private sealed record LoadedBacklogItem(
        string Id,
        string Title,
        string Body,
        string Status,
        DateTimeOffset UpdatedAt,
        List<string> Notes);
}
