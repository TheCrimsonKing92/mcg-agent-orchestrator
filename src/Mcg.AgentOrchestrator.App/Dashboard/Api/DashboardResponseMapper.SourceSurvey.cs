using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardResponseMapper
{
public static SourceSurveyDto ToSourceSurveyDto(SourceSurveyReport report)
{
    return new SourceSurveyDto(
        report.Root,
        report.MaxFiles,
        report.ReturnedFiles,
        report.TotalMatchedFiles,
        report.Files,
        report.Groups.Select(group => new SourceSurveyGroupDto(group.Directory, group.Count)).ToList(),
        report.ExcludedDirectoryNames,
        report.RecommendedCommand);
}
}
