using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CollaborationItemStore : ICollaborationItemStore
{
    private readonly string _dbPath;
    private readonly bool _readOnly;
    private static readonly TimeSpan DefaultActionTtl = TimeSpan.FromHours(12);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedActionVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "acceptance",
        "agent-add",
        "answer",
        "conduct",
        "doctor",
        "input-needed",
        "land",
        "next",
        "operator-inbox-ack",
        "recover",
        "re-delegate",
        "retry",
        "readiness",
        "refresh-dispatch",
        "subscription-plan",
        "verify",
        "verify-needed",
        "workspace"
    };

    public CollaborationItemStore(string dbPath)
        : this(dbPath, readOnly: false)
    {
    }

    private CollaborationItemStore(string dbPath, bool readOnly)
    {
        _dbPath = dbPath;
        _readOnly = readOnly;
        if (!readOnly)
        {
            EnsureSchema();
        }
    }

}
