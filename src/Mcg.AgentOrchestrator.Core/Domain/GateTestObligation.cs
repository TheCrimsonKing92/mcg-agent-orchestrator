namespace Mcg.AgentOrchestrator.Core;

public sealed record GateTestObligation(
    string Id, int BriefVersion, IReadOnlyList<string> Selectors,
    string? RequestingTaskId, string Question, DateTimeOffset RecordedAt)
{
    public static bool IsAcceptanceGateOwner(string? owner) =>
        string.Equals(owner?.Trim(), "acceptance-gate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(owner?.Trim(), "acceptance", StringComparison.OrdinalIgnoreCase);

    internal string RecordingNote =>
        $"GATE_TEST_OBLIGATION_RECORDED id={Id} brief_version={BriefVersion} selectors={Selectors.Count}";
}
