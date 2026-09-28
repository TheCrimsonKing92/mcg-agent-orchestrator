namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static (string Stdout, string Stderr) CreateCaptureFilePaths()
    {
        var directory = OrchestratorTempRoot.GetPurposeDirectory("acceptance-capture");
        return (Path.Combine(directory, $"mcg-acc-{Guid.NewGuid():N}.out"),
            Path.Combine(directory, $"mcg-acc-{Guid.NewGuid():N}.err"));
    }

    private static bool ShouldKeepCaptureFiles(bool? keepCaptureFiles, bool timedOut, int exitCode) =>
        keepCaptureFiles ?? (timedOut || exitCode != 0);
}
