using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliOwnerDigestLessons
{
    internal static void WriteText(TextWriter writer, OrchestratorWorkspace workspace,
        DateTimeOffset since, DateTimeOffset until)
    {
        if (!File.Exists(workspace.OperatorLessonsStorePath)) return;
        var lessons = new SqliteOperatorLessonStore(workspace.OperatorLessonsStorePath).List(includeRetired: true);
        var recorded = lessons.Where(lesson => lesson.RecordedAt >= since && lesson.RecordedAt < until)
            .OrderBy(lesson => lesson.RecordedAt).ThenBy(lesson => lesson.Id).ToArray();
        var retired = lessons.Count(lesson => lesson.RetiredAt >= since && lesson.RetiredAt < until);
        writer.WriteLine($"Lessons: recorded={recorded.Length} retired={retired}");
        foreach (var lesson in recorded)
            writer.WriteLine($"{lesson.Id} | {(lesson.Rule.Length <= 80 ? lesson.Rule : lesson.Rule[..79] + "…")}");
    }
}
