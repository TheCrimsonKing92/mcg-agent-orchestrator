using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MergeTrainFallbackAttribution
{
    internal static MergeTrainMemberBinding? TryAttribute(
        IReadOnlyList<string> resultPaths, string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members, int attempt,
        out IReadOnlyList<string> subjects, out string source)
    {
        var attributed = MergeTrainImpactAttribution.TryAttribute(resultPaths, workspacePath, members, out subjects);
        source = "impact";
        if (attributed is not null) return attributed;
        if (attempt == 0)
        {
            attributed = MergeTrainReachAttribution.TryAttribute(resultPaths, workspacePath, members, out subjects);
            source = "reach";
            if (attributed is not null) return attributed;
        }
        attributed = MergeTrainPathAttribution.TryAttribute(resultPaths, workspacePath, members, out subjects);
        source = "path";
        if (attributed is not null) return attributed;
        subjects = [];
        source = "ownership";
        return null;
    }
}
