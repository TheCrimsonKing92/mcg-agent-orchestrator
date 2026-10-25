namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static IDisposable BeginActivationAdmissionHealth(bool emit) => ActivationAdmissionAccumulator.Begin(emit);

    private static void RecordActivationAdmissionFailure() => ActivationAdmissionAccumulator.Current.Value?.Record(failed: true);
    private static void RecordActivationAdmissionWaiting(int waiting) => ActivationAdmissionAccumulator.Current.Value?.Record(waiting: waiting);
    private static void RecordActivationAdmissionCensus(int occupied, int live) =>
        ActivationAdmissionAccumulator.Current.Value?.Record(occupied: occupied, live: live,
            evidenceHeld: occupied >= 1 && live == 0 ? ActivationAdmissionPermitHolders.CountEvidenceHeld() : 0);

    private static void EmitActivationAdmissionHealth(bool emit, int tick)
    {
        if (emit && ActivationAdmissionAccumulator.Current.Value is { } current)
            EmitProgress(current.Take(tick).Format());
    }
}
