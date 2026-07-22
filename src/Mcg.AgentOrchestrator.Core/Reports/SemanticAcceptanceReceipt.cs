using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed record SemanticJudgeReceiptEntry(
    string Judge,
    bool Valid,
    bool CriteriaMet);

public sealed record SemanticAcceptanceReceipt(
    DateTimeOffset At,
    string GoalId,
    bool? Consensus,
    IReadOnlyList<SemanticJudgeReceiptEntry> Judges);
