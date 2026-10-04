using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ReviewerChangedExistingTestClassifier
{
    internal static IReadOnlyList<ChangedExistingTest> Classify(
        IReadOnlyList<ChangedExistingTest> entries, IEnumerable<HumanInputRequest> requests, GoalId goalId)
    {
        var rulings = requests.Where(request => request.GoalId == goalId && request.IsCompleted &&
                !request.WasDismissed && request.SupersededByRequestId is null &&
                !request.IsSyntheticParkedHumanWaitCompletion &&
                HumanWaitPolicyDefaults.IsSpecClarificationClass(request.Kind))
            .OrderBy(request => request.AnsweredAt).ThenBy(request => request.Id.Value, StringComparer.Ordinal)
            .Select(request => (Id: request.Id.Value, Ruling: FrozenFactRuling.TryParse(request.AuthoritativeAnswer?.Text)))
            .Where(entry => entry.Ruling is not null).ToArray();
        return entries.Select(entry =>
        {
            var frozen = rulings.FirstOrDefault(ruling => ruling.Ruling!.FrozenClasses
                .Any(type => SimpleName(type) == entry.TypeName));
            // Removing a frozen fact cannot be authorized by an amendment to that fact.
            if (entry.Removed && frozen.Ruling is not null)
                return entry with { Class = ChangedExistingTestClass.FrozenClassViolation, RulingRequestId = frozen.Id };
            var amended = rulings.FirstOrDefault(ruling => ruling.Ruling!.AmendedFacts
                .Any(fact => SimpleFact(fact.Fact) == entry.TypeName + "." + entry.MethodName));
            if (amended.Ruling is not null)
                return entry with { Class = ChangedExistingTestClass.AllowedByRuling, RulingRequestId = amended.Id };
            return frozen.Ruling is not null
                ? entry with { Class = ChangedExistingTestClass.FrozenClassViolation, RulingRequestId = frozen.Id }
                : entry with { Class = ChangedExistingTestClass.NoRuling, RulingRequestId = null };
        }).ToArray();
    }

    private static string SimpleName(string name) => name.Split('.').Last();
    private static string SimpleFact(string fact) => string.Join(".", fact.Split('.').TakeLast(2));
}

internal static class ReviewerChangedExistingTestScope
{
    internal static ReviewerChangedExistingTestRead Read(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string workingDirectory,
        string? mergeBase, string? headCommit, IReadOnlyList<string>? changedPaths)
    {
        if (task.RequiredRole != AgentRole.Reviewer) return new([], null);
        var read = ReviewerChangedExistingTestReader.Read(workingDirectory, mergeBase, headCommit, changedPaths);
        var classified = ReviewerChangedExistingTestClassifier.Classify(read.Entries, kernel.HumanInputRequests, goal.Id);
        return new(classified, read.Diagnostic);
    }

    internal static void RecordConductEvent(
        Goal goal, TaskSpec task, ReviewerChangedExistingTestRead read, string promptRoot,
        DateTimeOffset dispatchedAt, bool allowPendingRecordedDispatchRefresh)
    {
        if (task.RequiredRole != AgentRole.Reviewer || allowPendingRecordedDispatchRefresh) return;
        var violations = read.Entries.Where(entry => entry.Class == ChangedExistingTestClass.FrozenClassViolation)
            .OrderBy(entry => entry.File, StringComparer.Ordinal).ThenBy(entry => entry.StartLine)
            .ThenBy(entry => entry.TypeName, StringComparer.Ordinal)
            .ThenBy(entry => entry.MethodName, StringComparer.Ordinal).ToArray();
        if (violations.Length > 0)
        {
            var taskNumber = goal.Tasks.ToList().FindIndex(candidate => candidate.Id == task.Id) + 1;
            var detail = $"FROZEN_FACT_SCOPE goal={goal.Id.Value[..8]} task={taskNumber} violations={violations.Length} facts={string.Join(",", violations.Select(entry => entry.TypeName + "." + entry.MethodName))}";
            AppendConductEvent(promptRoot, new(dispatchedAt, "frozen-fact-scope", goal.Id.Value, detail));
        }
    }

    private static void AppendConductEvent(string promptRoot, ConductEventMirrorRecord record)
    {
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(promptRoot))!, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "conduct-events.log"),
                JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
