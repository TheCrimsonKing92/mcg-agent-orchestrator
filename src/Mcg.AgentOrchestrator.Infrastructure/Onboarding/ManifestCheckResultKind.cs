namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ManifestCheckResultKind
{
    Match,
    RunnerDiffers,
    MissingFromLearned,
    MissingFromManifest,
    Unresolved
}
