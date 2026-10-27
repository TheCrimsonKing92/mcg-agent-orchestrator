using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetTestCheckTranslator
{
    internal static AcceptanceManifestCheck FromImpactCheck(
        string worktreePath,
        RepositoryTestImpactCheck check)
    {
        // Command format: ["dotnet", "test", "--project", <project>, ...args]
        // or the legacy positional form: ["dotnet", "test", <optional project>, ...args]
        return FromCommand(worktreePath, check.Name, [.. check.Command]);
    }

    internal static AcceptanceManifestCheck FromCommand(
        string worktreePath,
        string name,
        string[] command)
    {
        var remaining = command.Skip(2).ToArray();
        var usesNativeProjectOption = remaining.Length > 1 &&
            remaining[0].Equals("--project", StringComparison.OrdinalIgnoreCase);
        var project = usesNativeProjectOption
            ? remaining[1]
            : remaining.Length > 0 && !remaining[0].StartsWith("-", StringComparison.Ordinal)
                ? remaining[0]
                : null;
        var arguments = usesNativeProjectOption
            ? remaining.Skip(2).ToArray()
            : project is null
                ? remaining
                : remaining.Skip(1).ToArray();
        return new AcceptanceManifestCheck
        {
            Name = name,
            Type = "dotnet-test",
            Project = project,
            Arguments = arguments,
            Runner = ResolveRunner(worktreePath, project)
        };
    }

    internal static AcceptanceManifestCheck? TryFromCommandLine(
        string worktreePath,
        string name,
        string commandLine)
    {
        var parts = SplitCommandLine(commandLine);
        if (parts.Length < 2 ||
            !parts[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            !parts[1].Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return FromCommand(worktreePath, name, parts);
    }

    internal static string ResolveRunner(
        string worktreePath,
        string? project,
        Func<string, XDocument>? loadProject = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) ||
            string.IsNullOrWhiteSpace(project) ||
            !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return "mtp";
        }

        try
        {
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
            var projectPath = Path.GetFullPath(Path.Combine(rootPath, project));
            var relativePath = Path.GetRelativePath(rootPath, projectPath);
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Equals("..", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return "mtp";
            }

            var document = (loadProject ?? (path => XDocument.Load(path, LoadOptions.None)))(projectPath);
            var declarations = document
                .Descendants()
                .Where(element => element.Name.LocalName.Equals(
                    "UseMicrosoftTestingPlatformRunner",
                    StringComparison.Ordinal))
                .ToArray();
            if (declarations.Length != 1)
            {
                return "mtp";
            }

            var declaration = declarations[0];
            var propertyGroup = declaration.Parent;
            var isUnconditionalTopLevelProperty =
                propertyGroup is not null &&
                propertyGroup.Name.LocalName.Equals("PropertyGroup", StringComparison.Ordinal) &&
                ReferenceEquals(propertyGroup.Parent, document.Root) &&
                !propertyGroup.Attributes().Any(attribute =>
                    attribute.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase)) &&
                !declaration.Attributes().Any(attribute =>
                    attribute.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase));
            return isUnconditionalTopLevelProperty &&
                bool.TryParse(declaration.Value.Trim(), out var useMtp) &&
                !useMtp
                    ? "vstest"
                    : "mtp";
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                System.Xml.XmlException or
                ArgumentException or
                NotSupportedException)
        {
            return "mtp";
        }
    }

    private static string[] SplitCommandLine(string commandLine) =>
        commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
