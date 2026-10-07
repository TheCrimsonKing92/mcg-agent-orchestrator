namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal sealed partial class AcceptanceManifestCheck
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsResolvedInfrastructureLane { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool RequiresBuildSystemChange { get; init; }
    }

    private static IReadOnlyList<AcceptanceTestLane> ResolveOwnedCollectionLanes(
        AcceptanceGateEngineSettings settings, string worktreePath) =>
        AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, worktreePath);

    private static AcceptanceManifestCheck BuildInfrastructureShardCheck(
        AcceptanceManifestCheck check,
        AcceptanceTestLane lane) =>
        new()
        {
            Name = $"{check.Name}: {lane.Name}",
            Type = check.Type,
            Command = check.Command,
            Project = check.Project,
            Arguments = [.. check.Arguments, "--filter", lane.Filter],
            Pattern = check.Pattern,
            FilePath = check.FilePath,
            TimeoutMinutes = check.TimeoutMinutes,
            Advisory = check.Advisory,
            Runner = check.Runner,
            EstimatedSerialSeconds = lane.EstimatedSerialSeconds,
            ExclusiveResourceKeys = lane.ExclusiveResourceKeys,
            RequiresBuildSystemChange = lane.RequiresBuildSystemChange,
            IsResolvedInfrastructureLane = true
        };

    private static IEnumerable<string> TranslateCheckMtpFilter(
        AcceptanceManifestCheck check, string filter) =>
        check.IsResolvedInfrastructureLane
            ? AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter)
            : TranslateMtpFilter(filter);
}
