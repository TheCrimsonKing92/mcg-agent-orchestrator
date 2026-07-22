using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CollaborationItemStore
{
    private static NotificationDelivery ReadNotificationDelivery(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)));

    private static EffectReceipt ReadEffectReceipt(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            new DecisionActionRef(reader.GetString(3)),
            Enum.Parse<EffectReceiptStatus>(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)));

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("Failed to deserialize collaboration decision payload.");

    private static CollaborationItem ReadItem(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            Enum.Parse<CollaborationItemType>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Enum.Parse<CollaborationItemStatus>(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static CollaborationBoundAction ReadAction(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4) != 0,
            reader.GetInt32(5) != 0,
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static CollaborationDecisionAuditEntry ReadAudit(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            DateTimeOffset.Parse(reader.GetString(11)));
}
