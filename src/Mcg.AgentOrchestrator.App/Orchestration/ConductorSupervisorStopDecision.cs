using System.Security;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorSupervisorStopDecision
{
    internal static string? Basis(string? stopFilePath, ConductorContinuityExitArtifact? artifact)
    {
        if (!string.IsNullOrEmpty(stopFilePath))
        {
            try
            {
                if (File.Exists(stopFilePath))
                {
                    return "stop-file-present";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
            {
                // An invalid or inaccessible path does not establish a stop signal.
            }
        }

        return artifact?.StopReason is "stop-file" or "stop-file-during-sleep"
            ? $"exit-reason={artifact.StopReason}"
            : null;
    }
}
