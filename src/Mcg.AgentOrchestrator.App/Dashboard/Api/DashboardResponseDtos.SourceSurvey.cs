namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record SourceSurveyDto(
    string Root,
    int MaxFiles,
    int ReturnedFiles,
    int TotalMatchedFiles,
    IReadOnlyList<string> Files,
    IReadOnlyList<SourceSurveyGroupDto> Groups,
    IReadOnlyList<string> ExcludedDirectoryNames,
    string RecommendedCommand,
    string InventorySource,
    bool TraversalComplete,
    IReadOnlyList<string> IncompleteReasons);

internal sealed record SourceSurveyGroupDto(string Directory, int Count);
