using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

try
{
    if (args.Length < 2) throw new ArgumentException("Expected worktree root and result path, followed by requested projects.");
    var offset = 2;
    if (args.Length > 3 && args[2] == "--initialize-legacy-goal")
    {
        // Explicit operator bridge for worktrees dispatched before scope metadata was introduced.
        // Never replace existing pinned metadata on an ordinary build.
        if (!File.Exists(WorkerBuildProjectScope.MetadataPath(args[0])))
        {
            var goal = Path.GetFileName(Path.TrimEndingDirectorySeparator(args[0]));
            WorkerBuildProjectScope.Prepare(args[0], goal, "legacy-operator", args[3]);
        }
        offset = 4;
    }
    var result = WorkerBuildProjectScope.Resolve(args[0], args.Skip(offset));
    File.WriteAllText(args[1], JsonSerializer.Serialize(result));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"worker build scope: {exception.Message}");
    return 1;
}
