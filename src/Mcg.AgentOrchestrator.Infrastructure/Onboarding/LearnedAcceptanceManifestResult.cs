using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record LearnedAcceptanceManifestResult(
    string? Json,
    IReadOnlyList<ManifestBlocker> Blockers,
    IReadOnlyList<ProjectOwnerQuestion> OwnerQuestions);
