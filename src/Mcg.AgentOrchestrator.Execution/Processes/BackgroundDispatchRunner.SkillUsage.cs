using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    internal WorkerSkillTranscriptReader SkillTranscripts { get; init; } = WorkerSkillTranscriptReader.CreateDefault();

    private string ResolveSkillsReceipt(GoalId goal, TaskId task, TaskDispatchRecord? dispatch,
        string stdoutPath, TaskVerificationRecord verification)
    {
        IReadOnlyList<string>? read = null;
        try { read = SkillTranscripts.Read(dispatch, stdoutPath); }
        catch (Exception) { /* A failed observation is still recorded as unavailable. */ }
        IReadOnlyList<string> claimed = [];
        try
        {
            if (new WorkerResultContractParser().TryParseWorkerResultContract(verification, out var fields) &&
                fields.TryGetValue("skills", out var value)) claimed = SkillNames(value.Split(','));
        }
        catch (Exception) { /* Claims are advisory telemetry. */ }
        return $"{RoundValueSkillSlice.NotePrefix}goal={goal.Value[..Math.Min(8, goal.Value.Length)]} task={task.Value[..Math.Min(8, task.Value.Length)]} " +
            $"selected={List(dispatch?.SelectedSkills ?? [])} read={(read is null ? "unavailable" : List(read))} claimed={List(claimed)}";
    }

    private static string List(IEnumerable<string> skills)
    {
        var names = SkillNames(skills);
        return names.Count == 0 ? "none" : string.Join(',', names);
    }

    private static IReadOnlyList<string> SkillNames(IEnumerable<string> names) => names.Where(n => n is not null).Select(n => n.Trim())
        .Where(n => !n.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(n, @"\A[a-z0-9][a-z0-9_-]*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .Select(n => n.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static void RecordSkillsReceipt(AgentOrchestratorKernel kernel, GoalId goal, TaskId task, string receipt)
    {
        try { kernel.RecordTaskNote(goal, task, receipt); }
        catch (Exception) { /* Telemetry must never fail refresh. */ }
    }
}
