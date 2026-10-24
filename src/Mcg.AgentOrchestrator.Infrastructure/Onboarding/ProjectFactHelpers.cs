using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ProjectFactHelpers
{
    internal static bool IsSafePath(string root, string path)
    {
        var relative = Relative(root, path);
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    internal static ProjectFact<T> Fact<T>(T value, FactConfidence confidence, FactSource source,
        string key, string reason, List<ProjectOwnerQuestion> questions)
    {
        if (confidence != FactConfidence.High)
            Ask(key, reason, source, questions);
        return new ProjectFact<T>(value, source, confidence);
    }

    internal static void Ask(string key, string reason, FactSource source, List<ProjectOwnerQuestion> questions) =>
        questions.Add(new ProjectOwnerQuestion(key, $"{key}: {reason}", source));
}
