namespace Mcg.AgentOrchestrator.Core;

/// <summary>A per-discovery snapshot; identities and schema metadata are not inferred facts.
/// RepositoryRoot labels the logical root ("."), rather than persisting the host's absolute path.</summary>
public sealed record ProjectModel(
    int SchemaVersion,
    string RepositoryRoot,
    IReadOnlyList<ProjectUnit> Units,
    IReadOnlyList<UnitDependency> Dependencies,
    IReadOnlyList<UnitTestSetup> TestSetups,
    IReadOnlyList<ProjectOwnerQuestion> OwnerQuestions,
    IReadOnlyList<UnitCommands> Commands,
    IReadOnlyList<EnvironmentNeed> EnvironmentNeeds,
    IReadOnlyList<UnitMeasurement> Measurements,
    IReadOnlyList<SharedStateHazard> Hazards)
{
    public const int CurrentSchemaVersion = 3;
}
