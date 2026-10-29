namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Display facts, never a gate evaluator. Each section retains its own authority and identity.
internal sealed record OwnerGoalSummarySection(string State, IReadOnlyList<string> Items,
    string Source, int Omitted = 0, IReadOnlyList<string>? ExpandedItems = null);

// Raw facts already loaded for goal detail; preview wording never derives from diagnostic states.
internal enum OwnerGoalPreviewChangeKind { None, Unavailable, Listed }
internal enum OwnerGoalPreviewEvidenceKind { Gate, Focused }
internal sealed record OwnerGoalPreviewChanges(OwnerGoalPreviewChangeKind Kind, IReadOnlyList<string> Files, int Total);
internal sealed record OwnerGoalPreviewEvidence(OwnerGoalPreviewEvidenceKind Kind, bool Passed, DateTimeOffset At,
    string? FirstFailedCheck = null);
internal sealed record OwnerGoalPreviewFacts(string Title, OwnerGoalPreviewChanges Changes, OwnerGoalPreviewEvidence? Evidence);

internal sealed record OwnerGoalChangeSummary(string GoalId, int BriefVersion, string CriteriaIdentity,
    string? Candidate, OwnerGoalSummarySection Planned, OwnerGoalSummarySection Changed,
    OwnerGoalSummarySection Reported, OwnerGoalSummarySection Verified, string Landing)
{
    internal OwnerGoalPreviewFacts? Preview { get; init; }

    internal OwnerGoalChangeSummary AsStale() => this with
    {
        Planned = Planned with { State = "[stale — refresh queued] " + Planned.State },
        Changed = Changed with { State = "[stale — refresh queued] " + Changed.State },
        Reported = Reported with { State = "[stale — refresh queued] " + Reported.State },
        Verified = Verified with { State = "[stale — refresh queued] " + Verified.State },
        Landing = "[stale — refresh queued] " + Landing
    };

    internal IEnumerable<string> DetailLines()
    {
        foreach (var (name, section) in new[] { ("Planned", Planned), ("Changed", Changed),
                     ("Reported", Reported), ("Verified", Verified) })
        {
            yield return name + ": " + section.State;
            foreach (var item in section.Items) yield return "  • " + item;
            if (section.Omitted > 0) yield return $"  … {section.Omitted} more; open the cited source";
            yield return "  Source: " + section.Source;
        }
        yield return "Candidate: " + (Candidate is null ? "unavailable" : Candidate + " (last recorded committed candidate; ongoing edits excluded)");
        yield return $"Authority: brief v{BriefVersion}; criteria {CriteriaIdentity}";
        yield return "Promotion: " + Landing;
        var expanded = new[] { Planned, Changed, Reported }.Where(section => section.ExpandedItems is { Count: > 0 }).ToArray();
        if (expanded.Length > 0)
        {
            yield return "Expanded source text (scroll; retained bounded excerpt):";
            foreach (var section in expanded)
                foreach (var item in section.ExpandedItems!) yield return "  " + item;
        }
    }

    internal IReadOnlyList<string> PreviewLines =>
    [
        "Planned: " + Planned.State + (Planned.Items.Count > 0 ? " — " + Planned.Items[0] : ""),
        "Changed: " + Changed.State + (Changed.Items.Count > 0 ? " — " + Changed.Items[0] : ""),
        "Verified: " + Verified.State + "; " + Landing + " (Enter: Goal detail)"
    ];
}
