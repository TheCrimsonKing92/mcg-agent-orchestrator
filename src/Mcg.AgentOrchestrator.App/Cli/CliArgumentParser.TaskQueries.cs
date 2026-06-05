using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
public static TaskQuery ParseTaskQuery(IEnumerable<string> tokens)
{
    WorkTaskStatus? status = null;
    AgentRole? role = null;
    string? idPrefix = null;
    TaskEvidenceKind? evidence = null;
    ProgressKind? eventKind = null;
    var parts = tokens.ToList();

    if (parts.Count % 2 != 0)
    {
        throw new ArgumentException(TaskQueryUsage());
    }

    for (var index = 0; index < parts.Count; index += 2)
    {
        var field = parts[index].ToLowerInvariant();
        var value = parts[index + 1];

        switch (field)
        {
            case "status":
                status = ParseTaskQueryStatus(value);
                break;
            case "role":
                role = ParseAgentRole(value);
                break;
            case "id":
                idPrefix = value;
                break;
            case "evidence":
                evidence = ParseTaskEvidenceKind(value);
                break;
            case "event":
                eventKind = ParseProgressKind(value);
                break;
            default:
                throw new ArgumentException(TaskQueryUsage());
        }
    }

    return new TaskQuery(status, role, idPrefix, evidence, eventKind);
}

public static string TaskQueryUsage()
{
    return "Usage: tasks [status <status>] [role <role>] [id <task-id-prefix>] [evidence <kind>] [event <kind>]";
}
}
