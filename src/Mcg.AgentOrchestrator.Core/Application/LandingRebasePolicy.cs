namespace Mcg.AgentOrchestrator.Core;

public enum LandingRebaseAction { Proceed, Retire, Escalate }

/// <summary>Observed rebase result; status names keep this policy independent of workspace effects.</summary>
public sealed record LandingRebaseFacts(
    string Phase, bool UpdatedBranch, string RebaseStatus, IReadOnlyList<string> ConflictFiles,
    string Message, bool ApplySideEffects)
{
    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("phase", Phase), new("updatedBranch", UpdatedBranch ? "true" : "false"),
        new("rebaseStatus", RebaseStatus), new("conflictFiles", string.Join("\n", ConflictFiles)),
        new("message", Message), new("applySideEffects", ApplySideEffects ? "true" : "false")
    ]);

    public static LandingRebaseFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 6)
            throw new InvalidOperationException("Landing rebase replay requires exactly six named facts.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
            if (!values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException($"Duplicate landing rebase fact '{fact.Name}'.");
        string Read(string name) => values.TryGetValue(name, out var value) ? value
            : throw new InvalidOperationException($"Missing landing rebase fact '{name}'.");
        bool ReadBool(string name) => Read(name) switch
        {
            "true" => true, "false" => false,
            _ => throw new InvalidOperationException($"Invalid boolean landing rebase fact '{name}'.")
        };
        var files = Read("conflictFiles");
        var restored = new LandingRebaseFacts(Read("phase"), ReadBool("updatedBranch"), Read("rebaseStatus"),
            Array.AsReadOnly(files == "" ? Array.Empty<string>() : files.Split('\n')), Read("message"), ReadBool("applySideEffects"));
        ValidatePhase(restored.Phase);
        return restored;
    }

    internal static void ValidatePhase(string phase)
    {
        if (phase is not ("pre-landing" or "pre-merge"))
            throw new InvalidOperationException("Invalid landing rebase phase fact.");
    }
}

public sealed record LandingRebaseDecision(
    LandingRebaseAction Action, int DiscriminatingRung, string DiscriminatingEvidence,
    string Reason, LandingRebaseFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(LandingRebasePolicy.StageName, Action.ToString(),
        DiscriminatingRung, DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Selects the shared pre-landing and pre-merge rebase disposition without effects.</summary>
public static class LandingRebasePolicy
{
    public const string StageName = "landing-rebase";

    public static LandingRebaseDecision Evaluate(LandingRebaseFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        LandingRebaseFacts.ValidatePhase(facts.Phase);
        if (facts.UpdatedBranch)
            return new(LandingRebaseAction.Proceed, 0, "rebase-updated-branch", "Rebase updated the goal branch.", facts);
        if (facts.RebaseStatus == "MissingBranch")
            return new(LandingRebaseAction.Retire, 1, "missing-branch-retired",
                $"Conductor tick retired missing goal branch before landing because the goal artifact could not be rebased: {facts.Message}", facts);
        if (facts.RebaseStatus == "Conflict")
            return new(LandingRebaseAction.Escalate, 2, "rebase-conflict",
                $"{facts.Phase} rebase conflict ({string.Join(", ", facts.ConflictFiles)}); use 'workspace rebase' to resolve", facts);
        return new(LandingRebaseAction.Escalate, 3, "rebase-failed", $"{facts.Phase} rebase failed: {facts.Message}", facts);
    }
}
