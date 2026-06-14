using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class OperatorDecisionLog
{
    private const string FileName = "operator-decisions.json";

    public static bool TryRecord(string auditDirectory, OperatorDecision decision, DateTimeOffset decidedAt)
    {
        var entries = Load(auditDirectory);
        if (entries.Any(e => e.InboxItemId.Equals(decision.InboxItemId, StringComparison.OrdinalIgnoreCase)))
            return false;

        entries.Add(new AuditEntry(
            decision.InboxItemId,
            decision.Command,
            decision.FreeText,
            decision.ActorId,
            decision.IdempotencyKey,
            decidedAt));
        Save(auditDirectory, entries);
        return true;
    }

    public static bool IsRecorded(string auditDirectory, string inboxItemId)
    {
        return Load(auditDirectory)
            .Any(e => e.InboxItemId.Equals(inboxItemId, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<AuditEntry> LoadAll(string auditDirectory) => Load(auditDirectory);

    private static List<AuditEntry> Load(string auditDirectory)
    {
        var path = StorePath(auditDirectory);
        if (!File.Exists(path))
            return [];
        try
        {
            var store = JsonSerializer.Deserialize<AuditStore>(File.ReadAllText(path), JsonOptions());
            return store?.Entries?.ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Save(string auditDirectory, List<AuditEntry> entries)
    {
        Directory.CreateDirectory(auditDirectory);
        File.WriteAllText(
            StorePath(auditDirectory),
            JsonSerializer.Serialize(new AuditStore(entries), JsonOptions()));
    }

    private static string StorePath(string auditDirectory) => Path.Combine(auditDirectory, FileName);

    private static JsonSerializerOptions JsonOptions() =>
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public sealed record AuditEntry(
        string InboxItemId,
        string Command,
        string? FreeText,
        string ActorId,
        string IdempotencyKey,
        DateTimeOffset DecidedAt);

    private sealed record AuditStore(IReadOnlyList<AuditEntry> Entries);
}
