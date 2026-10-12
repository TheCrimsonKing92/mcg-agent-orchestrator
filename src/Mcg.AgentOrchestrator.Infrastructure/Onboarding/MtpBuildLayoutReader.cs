using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Refuses layouts that cannot use the standard MTP artifacts path template.</summary>
public static class MtpBuildLayoutReader
{
    private static readonly HashSet<string> LayoutElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "TargetFrameworks", "RuntimeIdentifier", "RuntimeIdentifiers", "OutputPath", "BaseOutputPath",
        "ArtifactsPath", "UseArtifactsOutput", "ArtifactsPivots", "AppendTargetFrameworkToOutputPath",
        "AppendRuntimeIdentifierToOutputPath"
    };

    public static string? ReadRefusal(string sourceRoot, string projectPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var projectFile = Path.GetFullPath(Path.Combine(root, projectPath));
        var refusal = ReadFile(projectFile, optional: false);
        if (refusal is not null) return refusal;

        var relative = Path.GetRelativePath(root, projectFile);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var directory = Path.GetDirectoryName(projectFile)!; ; directory = Path.GetDirectoryName(directory)!)
        {
            refusal = ReadFile(Path.Combine(directory, "Directory.Build.props"), optional: true)
                ?? ReadFile(Path.Combine(directory, "Directory.Build.targets"), optional: true);
            if (refusal is not null) return refusal;
            if (directory.Equals(root, comparison)) return null;
        }

        string? ReadFile(string file, bool optional)
        {
            try
            {
                using var stream = File.OpenRead(file);
                var document = XDocument.Load(stream);
                var element = document.Descendants().FirstOrDefault(element => LayoutElements.Contains(element.Name.LocalName));
                return element is null ? null
                    : $"MTP project {projectPath}: file {file} declares layout element {element.Name.LocalName}";
            }
            catch (Exception exception) when (optional && exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
            {
                return $"MTP project {projectPath}: cannot read XML file {file}: {exception.Message}";
            }
        }
    }
}
