namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class StructuralCoverageFailureMessage
{
    internal static string Build(string worktreePath, string? projectHomeDirectory,
        IReadOnlyCollection<string> undeclaredTestProjects) =>
        $"Structural coverage requires every discovered test project to be declared by a dotnet-test check in {AcceptanceManifestDisplayPathResolver.Resolve(worktreePath, projectHomeDirectory)}:" +
        Environment.NewLine + string.Join(Environment.NewLine, undeclaredTestProjects);
}
