namespace Mcg.AgentOrchestrator.Core;

public enum VerifyingStageAction
{
    Hold,
    Proceed
}

/// <summary>Observed acceptance inputs; null means the driver has not evaluated that input.</summary>
public sealed record VerifyingStageFacts(
    GoalLifecycleState CallerState,
    bool IsConductorTick,
    GoalStatus GoalStatus,
    bool? ParallelAcceptanceEnabled = null,
    bool? CandidateBuilt = null,
    bool? ReplacementLeaseAvailable = null,
    string? ReplacementLeaseReason = null,
    bool? AdmissionAdmitted = null,
    string? AdmissionReason = null,
    bool? ArtifactWriterBusy = null,
    string? ArtifactWriterMessage = null,
    string? AttemptDecisionKind = null,
    string? AttemptId = null,
    string? NoTickWaitOutcome = null)
{
    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("callerState", CallerState.ToString()),
        new("conductorTick", Encode(IsConductorTick)),
        new("parallelAcceptance", Encode(ParallelAcceptanceEnabled)),
        new("candidate", Encode(CandidateBuilt)),
        new("replacementLease", Encode(ReplacementLeaseAvailable)),
        new("admission", Encode(AdmissionAdmitted)),
        new("artifactWriter", Encode(ArtifactWriterBusy)),
        new("goalStatus", GoalStatus.ToString()),
        new("replacementLeaseReason", ReplacementLeaseReason ?? string.Empty),
        new("admissionReason", AdmissionReason ?? string.Empty),
        new("artifactWriterMessage", ArtifactWriterMessage ?? string.Empty),
        new("attemptDecision", AttemptDecisionKind ?? string.Empty),
        new("attemptId", AttemptId ?? string.Empty),
        new("noTickWait", NoTickWaitOutcome ?? string.Empty)
    ]);

    public static VerifyingStageFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 14)
            throw new InvalidOperationException("Verifying replay requires exactly fourteen named facts.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (!values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException($"Duplicate verifying fact '{fact.Name}'.");
        }

        string Read(string name) => values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing verifying fact '{name}'.");
        string? ReadOptional(string name) => Read(name) is { Length: > 0 } value ? value : null;
        bool? ReadBool(string name) => Read(name) switch
        {
            "true" => true,
            "false" => false,
            "" => null,
            _ => throw new InvalidOperationException($"Invalid boolean verifying fact '{name}'.")
        };
        T ReadEnum<T>(string name) where T : struct, Enum =>
            Enum.TryParse<T>(Read(name), out var value) && Enum.IsDefined(value) && value.ToString() == Read(name)
                ? value
                : throw new InvalidOperationException($"Invalid enum verifying fact '{name}'.");

        var attemptKind = ReadOptional("attemptDecision");
        if (attemptKind is not (null or "Started" or "Running" or "Completed" or "TerminalWithoutRun"))
            throw new InvalidOperationException("Invalid verifying attempt decision fact.");
        var waitOutcome = ReadOptional("noTickWait");
        if (waitOutcome is not (null or "deadline-elapsed" or "reconciliation-ownership-changed" or "ownership-changed"))
            throw new InvalidOperationException("Invalid verifying no-tick wait fact.");

        return new(
            ReadEnum<GoalLifecycleState>("callerState"),
            ReadBool("conductorTick") ?? throw new InvalidOperationException("Verifying replay requires conductorTick."),
            ReadEnum<GoalStatus>("goalStatus"), ReadBool("parallelAcceptance"), ReadBool("candidate"),
            ReadBool("replacementLease"), ReadOptional("replacementLeaseReason"),
            ReadBool("admission"), ReadOptional("admissionReason"),
            ReadBool("artifactWriter"), ReadOptional("artifactWriterMessage"),
            attemptKind, ReadOptional("attemptId"), waitOutcome);
    }

    private static string Encode(bool? value) => value switch { true => "true", false => "false", null => "" };
}

public sealed record VerifyingStageDecision(
    VerifyingStageAction Action,
    int DiscriminatingRung,
    string DiscriminatingEvidence,
    string Reason,
    VerifyingStageFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(
        VerifyingStagePolicy.StageName, Action.ToString(), DiscriminatingRung,
        DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Selects acceptance holds from observed facts without owning effects.</summary>
public static class VerifyingStagePolicy
{
    public const string StageName = "verifying";

    public static VerifyingStageDecision Evaluate(VerifyingStageFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        VerifyingStageDecision Hold(int rung, string evidence, string reason) =>
            new(VerifyingStageAction.Hold, rung, evidence, reason, facts);
        VerifyingStageDecision Proceed(string evidence, string reason) =>
            new(VerifyingStageAction.Proceed, 0, evidence, reason, facts);
        string Required(string? value, string name) => !string.IsNullOrEmpty(value)
            ? value
            : throw new InvalidOperationException($"Verifying hold requires {name}.");

        if (facts.CallerState == GoalLifecycleState.Verifying && !facts.IsConductorTick)
            return Hold(1, "conduct-loop-owned",
                "Acceptance gate is owned by the conduct loop; reconciliation will handle terminal artifact");

        if (facts.ParallelAcceptanceEnabled == false)
            return HandBack(2, "parallel-acceptance-disabled");

        if (facts.GoalStatus == GoalStatus.Completed)
            return Hold(3, "completed-goal",
                "Completed goal requires operator acceptance; background acceptance cannot reopen a landed goal.");

        if (facts.ReplacementLeaseAvailable == false)
            return Hold(4, "replacement-evidence-mutation-lease", Required(facts.ReplacementLeaseReason, "a lease reason"));

        if (facts.CandidateBuilt == false)
            return HandBack(5, "no-acceptance-candidate");

        if (facts.AdmissionAdmitted == false)
            return Hold(6, "acceptance-width-admission", Required(facts.AdmissionReason, "an admission reason"));

        if (facts.ArtifactWriterBusy == true)
            return Hold(7, "acceptance-artifact-writer-busy",
                $"Acceptance artifact writer busy; retry on next conduct tick. {facts.ArtifactWriterMessage}");

        if (!string.IsNullOrEmpty(facts.NoTickWaitOutcome))
        {
            var attemptId = Required(facts.AttemptId, "an attempt id");
            return facts.NoTickWaitOutcome switch
            {
                "deadline-elapsed" => Hold(8, "no-tick-wait-deadline",
                    $"Acceptance verification remains in background after bounded no-tick wait; attempt={attemptId}."),
                "reconciliation-ownership-changed" => Hold(8, "no-tick-reconciliation-ownership-changed",
                    $"Acceptance attempt reconciliation ownership changed; attempt={attemptId}."),
                "ownership-changed" => Hold(8, "no-tick-attempt-ownership-changed",
                    $"Acceptance attempt metadata or ownership changed; attempt={attemptId}."),
                _ => throw new InvalidOperationException("Unsupported verifying no-tick wait outcome.")
            };
        }

        if (facts.AttemptDecisionKind is "Started" or "Running")
        {
            var attemptId = Required(facts.AttemptId, "an attempt id");
            return Hold(9, "attempt-running-in-background",
                $"Acceptance verification running in background; attempt={attemptId}.");
        }

        return facts.AttemptDecisionKind switch
        {
            "Completed" => Proceed("attempt-completed", "Acceptance attempt completed."),
            "TerminalWithoutRun" => Proceed("attempt-terminal-without-run", "Acceptance attempt terminal without run."),
            _ => throw new InvalidOperationException("Verifying facts do not identify a hold or completed attempt.")
        };

        VerifyingStageDecision HandBack(int rung, string evidence) => facts.CallerState == GoalLifecycleState.Verifying
            ? Hold(rung, evidence, "Acceptance gate running in background; reconciliation will handle terminal artifact")
            : Proceed("fallback-not-applicable", "Fallback acceptance not applicable.");
    }
}
