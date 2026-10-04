using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class WorkerStoreReferenceResolver
{
    private static string? SelectExcerpt(StoreReference reference, string path, byte[] bytes)
    {
        switch (reference.Kind)
        {
            case "operator-evidence":
                return ReadText(bytes);
            case "goal-events":
                TryEventSelector(reference.Selector, out var type, out var contains);
                var lines = new List<string>();
                using (var reader = new StringReader(ReadText(bytes)))
                {
                    while (reader.ReadLine() is { } line)
                    {
                        if (contains is not null && !line.Contains(contains, StringComparison.Ordinal)) continue;
                        if (type is not null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            using var document = JsonDocument.Parse(line);
                            if (document.RootElement.ValueKind != JsonValueKind.Object
                                || !document.RootElement.TryGetProperty("eventType", out var eventType)
                                || eventType.ValueKind != JsonValueKind.String || eventType.GetString() != type) continue;
                        }
                        lines.Add(line);
                    }
                }
                return string.Join(Environment.NewLine, lines);
            case "trx":
                using (var stream = new MemoryStream(bytes, writable: false))
                    return string.Join(Environment.NewLine, AcceptanceTrxOutcomeTaxonomy.ExtractTrxFailureEvidence(
                        stream, includeFirstStackFrame: true, testNameSubstring: reference.Selector![5..]));
            case "attempt-result":
                using (var document = JsonDocument.Parse(bytes))
                {
                    var value = document.RootElement;
                    foreach (var segment in reference.Selector!.Split('.'))
                    {
                        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var property)) value = property;
                        else if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index)
                            && index >= 0 && index < value.GetArrayLength()) value = value[index];
                        else return null;
                    }
                    return value.GetRawText();
                }
            case "cohort-receipt":
            case "train-receipt":
                return ReadReceipt(reference, path);
            default:
                return null;
        }
    }

    private static string ReadText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? ReadReceipt(StoreReference reference, string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        var cohort = reference.Kind == "cohort-receipt";
        var table = cohort ? "cohort_receipts" : "merge_train_receipts";
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table";
        exists.Parameters.AddWithValue("$table", table);
        if (exists.ExecuteScalar() is null) return null;
        using var command = connection.CreateCommand();
        command.CommandText = cohort
            ? "SELECT * FROM cohort_receipts WHERE cohort_id=$id"
            : "SELECT * FROM merge_train_receipts WHERE train_id=$id";
        command.Parameters.AddWithValue("$id", reference.Locator);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = new Dictionary<string, object?>();
        for (var index = 0; index < reader.FieldCount; index++)
            row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        return JsonSerializer.Serialize(row);
    }
}
