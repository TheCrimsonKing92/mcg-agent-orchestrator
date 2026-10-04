using System.Globalization;

namespace Mcg.AgentOrchestrator.Core;

public enum DispatchAdmissionAction
{
    Admit,
    Hold
}

/// <summary>Observed admission inputs; a null slice result means it was not evaluated.</summary>
public sealed record DispatchAdmissionFacts(
    int RunningPaidWorkers,
    int ConfiguredWorkerCap,
    int AdmissionCapacity,
    int ReservedGateSlots,
    int EffectiveWorkerCap,
    int PolicyMaxConcurrentPaidWorkers,
    bool? SliceBatchAdmissionAllowed = null,
    string? SliceBatchAdmissionReason = null)
{
    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("running", RunningPaidWorkers.ToString(CultureInfo.InvariantCulture)),
        new("effectiveCap", EffectiveWorkerCap.ToString(CultureInfo.InvariantCulture)),
        new("configuredCap", ConfiguredWorkerCap.ToString(CultureInfo.InvariantCulture)),
        new("admissionCapacity", AdmissionCapacity.ToString(CultureInfo.InvariantCulture)),
        new("reservedGateSlots", ReservedGateSlots.ToString(CultureInfo.InvariantCulture)),
        new("policyMaxConcurrentPaidWorkers", PolicyMaxConcurrentPaidWorkers.ToString(CultureInfo.InvariantCulture)),
        new("sliceBatchAdmission", SliceBatchAdmissionAllowed switch
        {
            true => "allowed",
            false => "refused",
            null => "not-evaluated"
        }),
        new("sliceBatchReason", SliceBatchAdmissionReason ?? string.Empty)
    ]);

    public static DispatchAdmissionFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 8)
            throw new InvalidOperationException("Dispatch admission replay requires exactly eight named facts.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (!values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException($"Duplicate dispatch admission fact '{fact.Name}'.");
        }

        string Read(string name) => values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing dispatch admission fact '{name}'.");
        int ReadInt(string name) => int.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"Invalid integer dispatch admission fact '{name}'.");

        bool? sliceAllowed = Read("sliceBatchAdmission") switch
        {
            "allowed" => true,
            "refused" => false,
            "not-evaluated" => null,
            var value => throw new InvalidOperationException($"Invalid slice admission fact '{value}'.")
        };
        var sliceReason = Read("sliceBatchReason");
        return new DispatchAdmissionFacts(
            ReadInt("running"), ReadInt("configuredCap"), ReadInt("admissionCapacity"),
            ReadInt("reservedGateSlots"), ReadInt("effectiveCap"), ReadInt("policyMaxConcurrentPaidWorkers"),
            sliceAllowed, sliceAllowed == false || sliceReason.Length > 0 ? sliceReason : null);
    }
}

public sealed record DispatchAdmissionDecision(
    DispatchAdmissionAction Action,
    int DiscriminatingRung,
    string DiscriminatingEvidence,
    string Reason,
    DispatchAdmissionFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(
        DispatchAdmissionPolicy.StageName, Action.ToString(), DiscriminatingRung,
        DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Selects admission from observed facts without owning effects.</summary>
public static class DispatchAdmissionPolicy
{
    public const string StageName = "dispatch-admission";

    public static DispatchAdmissionDecision Evaluate(DispatchAdmissionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var running = facts.RunningPaidWorkers;
        var workerCap = facts.EffectiveWorkerCap;
        if (running >= workerCap)
        {
            var reservedGateSlot = facts.ReservedGateSlots > 0 && workerCap < facts.PolicyMaxConcurrentPaidWorkers;
            var admissionClamped = workerCap < facts.ConfiguredWorkerCap;
            var evidence = reservedGateSlot ? "reserved-gate-slot" : admissionClamped ? "worker-admission-capacity" : "worker-cap";
            var reason = reservedGateSlot
                ? $"At worker cap ({running}/{workerCap}) with a gate-ready goal reserving a stable slot; will advance when a slot opens"
                : admissionClamped
                    ? $"At worker admission capacity ({running}/{workerCap}); configured cap {facts.ConfiguredWorkerCap} is clamped; will advance when a slot opens"
                    : $"At worker cap ({running}/{workerCap}); will advance when a slot opens";
            return new(DispatchAdmissionAction.Hold, 1, evidence, reason, facts);
        }

        if (facts.SliceBatchAdmissionAllowed == false)
            return new(DispatchAdmissionAction.Hold, 2, "slice-batch-admission",
                facts.SliceBatchAdmissionReason ?? throw new InvalidOperationException("Slice admission refusal requires a reason."), facts);

        return new(DispatchAdmissionAction.Admit, 0, "admitted", "Dispatch admitted.", facts);
    }
}
