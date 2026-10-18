namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum DeveloperBranchIntegrationStatus
{
    Current,
    Integrated,
    Conflict,
    ConflictAdmitted,
    Failed
}

internal sealed record DeveloperBranchIntegrationResult(
    DeveloperBranchIntegrationStatus Status,
    string Message,
    IReadOnlyList<string> ConflictPaths,
    string? OriginalCandidateSha = null,
    string? IntegratedMainSha = null,
    string? ResultingCandidateSha = null)
{
    internal bool CanDispatch =>
        Status is DeveloperBranchIntegrationStatus.Current or DeveloperBranchIntegrationStatus.Integrated or DeveloperBranchIntegrationStatus.ConflictAdmitted;
}

