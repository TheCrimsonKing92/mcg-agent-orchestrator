namespace Mcg.AgentOrchestrator.Core;

/// <summary>A per-discovery snapshot; identities and schema metadata are not inferred facts.</summary>
public sealed record ProjectModel(
    int SchemaVersion,
    string RepositoryRoot,
    IReadOnlyList<ProjectUnit> Units,
    IReadOnlyList<UnitDependency> Dependencies,
    IReadOnlyList<UnitTestSetup> TestSetups,
    IReadOnlyList<ProjectOwnerQuestion> OwnerQuestions);
