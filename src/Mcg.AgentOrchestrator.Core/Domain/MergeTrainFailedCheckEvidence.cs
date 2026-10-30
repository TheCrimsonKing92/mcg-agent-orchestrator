namespace Mcg.AgentOrchestrator.Core;

// A failed check is covered by a focused recheck only when its own TRX evidence
// explains the failure and all of those fatal identities are selected again.
public sealed record MergeTrainFailedCheckEvidence(
    string CheckName, bool ExplainedByFatalTrx, IReadOnlyList<string> FatalTestIdentities);
