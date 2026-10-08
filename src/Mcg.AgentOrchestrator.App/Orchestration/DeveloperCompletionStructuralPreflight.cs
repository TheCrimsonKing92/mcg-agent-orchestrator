using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeveloperCompletionStructuralPreflight
{
    internal static DeveloperCompletionStructuralFindings Evaluate(string worktreePath,
        string? projectHomeDirectory = null)
    {
        var ratchet = SourceSizeRatchetPreflight.Evaluate(worktreePath);
        if (ratchet.HasBlockingViolation)
        {
            return new DeveloperCompletionStructuralFindings(true, ratchet.Message);
        }

        try
        {
            _ = AcceptanceGateEngineSettings.Load(worktreePath, projectHomeDirectory);
        }
        catch (InvalidDataException exception)
        {
            return new DeveloperCompletionStructuralFindings(
                true,
                $"config/acceptance-manifest.json: {exception.Message}");
        }

        return new DeveloperCompletionStructuralFindings(
            false,
            "source size ratchet and acceptance manifest lane consistency checks passed");
    }
}

internal sealed record DeveloperCompletionStructuralFindings(bool HasViolation, string Message);
