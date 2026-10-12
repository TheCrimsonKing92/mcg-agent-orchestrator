namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorSelfRelaunchAdmissionHold
{
    internal const string Reason = "self-relaunch-scheduled";

    internal static string Format(int tick) =>
        $"ADMISSION tick={tick} result=held reason={Reason}";
}
