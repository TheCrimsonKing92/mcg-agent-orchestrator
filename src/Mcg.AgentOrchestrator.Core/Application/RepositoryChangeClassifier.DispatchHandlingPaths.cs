namespace Mcg.AgentOrchestrator.Core;

public static partial class RepositoryChangeClassifier
{
    private static readonly (string Directory, string Stem)[] DispatchResultHandlingPaths =
    [
        ("src/Mcg.AgentOrchestrator.Core/", "AgentOutputDirectives"),
        ("src/Mcg.AgentOrchestrator.Core/Application/", "DispatchFailureClassifier"),
        ("src/Mcg.AgentOrchestrator.Infrastructure/Processes/", "BackgroundDispatch"),
        ("src/Mcg.AgentOrchestrator.Infrastructure/Processes/", "DispatchProcessHost"),
        ("src/Mcg.AgentOrchestrator.Infrastructure/Processes/", "GracefulDispatchDetacher"),
        ("src/Mcg.AgentOrchestrator.Infrastructure/Processes/", "ProcessLogReader.Decisions"),
        ("src/Mcg.AgentOrchestrator.Infrastructure/Processes/", "WorkerProcessJobs"),
        ("src/Mcg.AgentOrchestrator.Infrastructure/Workers/", "WorkerResultParser"),
        ("src/Mcg.AgentOrchestrator.Execution/Processes/", "BackgroundDispatch"),
        ("src/Mcg.AgentOrchestrator.Execution/Processes/", "DispatchProcessHost"),
        ("src/Mcg.AgentOrchestrator.Execution/Processes/", "GracefulDispatchDetacher"),
        ("src/Mcg.AgentOrchestrator.Execution/Processes/", "ProcessLogReader.Decisions"),
        ("src/Mcg.AgentOrchestrator.Execution/Processes/", "WorkerProcessJobs"),
        ("src/Mcg.AgentOrchestrator.Execution/Workers/", "WorkerResultParser")
    ];

    public static bool TouchesDispatchResultHandling(IEnumerable<string> paths) =>
        paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .Any(path => DispatchResultHandlingPaths.Any(pattern =>
                path.StartsWith(pattern.Directory, StringComparison.OrdinalIgnoreCase) &&
                path[(pattern.Directory.Length)..].StartsWith(pattern.Stem, StringComparison.OrdinalIgnoreCase) &&
                !path[(pattern.Directory.Length)..].Contains('/') &&
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)));
}
