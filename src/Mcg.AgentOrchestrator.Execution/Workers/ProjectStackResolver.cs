using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ProjectStackResolver
{
    public static Toolchain Resolve(string workingDirectory, string? projectHomeDirectory)
        => Resolve(workingDirectory, projectHomeDirectory, CrosswalkShadowDetect.Shared);

    internal static Toolchain Resolve(string workingDirectory, string? projectHomeDirectory, CrosswalkShadowDetect shadow)
    {
        if (!string.IsNullOrWhiteSpace(projectHomeDirectory))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectHomeDirectory, "project-model.json")));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("stack", out var declaration) &&
                    declaration.ValueKind == JsonValueKind.String &&
                    !int.TryParse(declaration.GetString(), out _))
                {
                    // Read only an explicit declaration: ProjectModel's default is not evidence.
                    var stack = declaration.Deserialize<ProjectStack>();
                    if (Enum.IsDefined(stack))
                        return stack == ProjectStack.Go ? Toolchain.Go : Toolchain.Dotnet;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                JsonException or ArgumentException or NotSupportedException)
            {
                // An unavailable or unusable declaration leaves marker detection authoritative.
            }
        }

        return shadow.Observe(workingDirectory, projectHomeDirectory, TargetToolchainDetector.Detect(workingDirectory));
    }

    internal static ProjectStack? GuidanceStack(Toolchain toolchain, bool isOrchestratorHome) => toolchain switch
    {
        Toolchain.Dotnet => ProjectStack.Dotnet,
        Toolchain.Go => ProjectStack.Go,
        _ => isOrchestratorHome ? ProjectStack.Dotnet : null
    };

    internal static ProjectStack? GuidanceStackFor(string workingDirectory, WorkerTargetHome? targetHome)
    {
        var home = targetHome ?? WorkerTargetHome.Home;
        return GuidanceStack(Resolve(workingDirectory, home.ProjectHomeDirectory), home.IsOrchestratorHome);
    }
}
