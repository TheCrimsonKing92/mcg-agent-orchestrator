using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    // Only creation call sites register here; store census and orphan adoption never do.
    internal static void NoteActivationProbeAdmission(AcceptanceHolderBinding binding, string resultPath,
        string? groupedMetadataPath = null) => ActivationAdmissionProbeScope.Current.Value?.Add(binding, resultPath, groupedMetadataPath);

    private static IDisposable BeginActivationAdmission(bool emit)
    {
        var scope = new ActivationAdmissionProbeScope(BeginActivationAdmissionHealth(emit));
        ActivationAdmissionProbeScope.Current.Value = emit ? scope : null;
        return scope;
    }

    private static void RecordActivationAdmissionWaitingForProbe(ParallelAcceptanceBatchState state)
    {
        RecordActivationAdmissionWaiting(state.OrderedEligible.Count);
        ActivationAdmissionProbeScope.Current.Value?.Waiting(census => census.Count(state.OrderedEligible, state.Results));
    }

    private static void EmitActivationAdmissionProbeAndHealth(bool emit, int tick)
    {
        if (emit && ActivationAdmissionProbeScope.Current.Value is { } current) EmitProgress(current.Take(tick).Format());
        EmitActivationAdmissionHealth(emit, tick);
    }
}
