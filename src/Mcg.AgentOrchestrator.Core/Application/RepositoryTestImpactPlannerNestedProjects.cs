namespace Mcg.AgentOrchestrator.Core;

internal static class RepositoryTestImpactPlannerNestedProjects
{
    internal sealed record Route(
        string Prefix, string Project, string CheckName, string FocusedName, RepositoryTestProject TestProject)
    {
        internal string[] Command => ["dotnet", "test", "--project", Project, "--verbosity", "minimal"];
    }

    internal static readonly IReadOnlyList<Route> Routes =
    [
        new("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/StorageRetention/",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/StorageRetention/Mcg.AgentOrchestrator.Infrastructure.StorageRetention.Tests.csproj",
            "storage retention tests", "focused changed storage retention tests", RepositoryTestProject.StorageRetention)
    ];
}
