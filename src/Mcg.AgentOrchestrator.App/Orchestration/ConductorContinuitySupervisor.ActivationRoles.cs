using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorContinuitySupervisor
{
    private static (string Primary, string Secondary) ActivationBuildRoles(string status) => status switch
    {
        "adopted" => ("adopted", "previous"),
        "suppressed" => ("suppressed", "current"),
        _ => ("failed", "restored")
    };

    private static string SerializeActivationPayload(
        string status,
        ConductorActivationBuild primary,
        ConductorActivationBuild? secondary,
        ConductorActivationRevertReason? reason,
        string detail,
        int attempt,
        DateTimeOffset occurredAt)
    {
        return status switch
        {
            "adopted" => JsonSerializer.Serialize(new
            {
                adoptedBuild = BuildPayload(primary),
                previousBuild = BuildPayload(secondary),
                reason = reason?.ToString(), detail, attempt, occurredAt
            }),
            "suppressed" => JsonSerializer.Serialize(new
            {
                suppressedBuild = BuildPayload(primary),
                currentBuild = BuildPayload(secondary),
                reason = reason?.ToString(), detail, attempt, occurredAt
            }),
            _ => JsonSerializer.Serialize(new
            {
                failedBuild = BuildPayload(primary),
                restoredBuild = BuildPayload(secondary),
                reason = reason?.ToString(), detail, attempt, occurredAt
            })
        };
    }

    private static object? BuildPayload(ConductorActivationBuild? build) => build is null
        ? null
        : new { commitSha = build.CommitSha, stagedBuildId = build.StagedBuildId };
}
