using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectCompareManifestEngineCommand
{
    internal const string Usage = "Usage: project compare-manifest-engine [name] [--root <path>] --manifest <path>";

    internal static int Execute(IReadOnlyList<string> parts, TextWriter output)
    {
        var manifestPath = ParseOptions(parts);
        IReadOnlyList<ManifestFactComparison> comparisons;
        try
        {
            var manifestJson = File.ReadAllText(Path.GetFullPath(manifestPath));
            comparisons = ManifestEngineSettingsComparer.Compare(manifestJson);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or JsonException or InvalidDataException)
        {
            throw new ArgumentException($"{Usage}{Environment.NewLine}Manifest could not be read: {exception.Message}", exception);
        }

        foreach (var comparison in comparisons)
            output.WriteLine($"{comparison.Kind} {comparison.Fact} hand={comparison.Hand} learned={comparison.Learned} ({comparison.Reason})");
        return comparisons.Any(comparison => comparison.Kind == ManifestFactResultKind.DiffersFromGateDefault) ? 1 : 0;
    }

    private static string ParseOptions(IReadOnlyList<string> parts)
    {
        string? root = null;
        string? manifest = null;
        var index = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal) ? 3 : 2;
        for (; index < parts.Count; index++)
        {
            var option = parts[index];
            var equals = option.IndexOf('=');
            var flag = equals < 0 ? option : option[..equals];
            if (flag is not "--root" and not "--manifest")
                throw new ArgumentException(Usage);
            var value = equals < 0 ? ++index < parts.Count ? parts[index] : null : option[(equals + 1)..];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            if (flag == "--root" && root is null)
                root = value;
            else if (flag == "--manifest" && manifest is null)
                manifest = value;
            else
                throw new ArgumentException(Usage);
        }
        return manifest ?? throw new ArgumentException(Usage);
    }
}
