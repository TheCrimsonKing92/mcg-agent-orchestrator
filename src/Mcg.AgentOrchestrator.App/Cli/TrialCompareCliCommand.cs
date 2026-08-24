using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class TrialCompareCliCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static void Execute(
        IReadOnlyList<string> parts,
        ITrialRootHost host,
        string defaultReceiptsDirectory,
        TextWriter output)
    {
        var specPath = ValueAfter(parts, "--spec") ??
            throw new ArgumentException(CliCommandHelp.TrialCompareUsage);
        var fullSpecPath = Path.GetFullPath(specPath);
        if (!File.Exists(fullSpecPath))
        {
            throw new FileNotFoundException($"Trial comparison spec does not exist: '{fullSpecPath}'.", fullSpecPath);
        }

        var format = ValueAfter(parts, "--format") ?? "text";
        if (!format.Equals("json", StringComparison.OrdinalIgnoreCase)
            && !format.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--format must be 'text' or 'json'.");
        }

        var request = ParseSpec(File.ReadAllText(fullSpecPath), parts, defaultReceiptsDirectory);
        var result = new TrialHarnessComparison(host).Run(request);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine(JsonSerializer.Serialize(result, Json));
        }
        else if (format.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"trial-compare succeeded={result.Succeeded} receipt={result.ReceiptPath}");
            foreach (var harness in result.Harnesses)
            {
                output.WriteLine($"  {harness.Name}: outcome={harness.Outcome} exit={harness.ExitCode?.ToString() ?? "unavailable"} stdout={harness.StdoutPath} stderr={harness.StderrPath}");
            }
        }
        if (!result.Succeeded)
        {
            throw new CliExitException(1);
        }
    }

    internal static TrialComparisonRequest ParseSpec(
        string json,
        IReadOnlyList<string> parts,
        string defaultReceiptsDirectory)
    {
        var spec = JsonSerializer.Deserialize<TrialCompareSpec>(json, Json) ??
            throw new ArgumentException("Trial comparison spec was empty.");
        var timeoutSeconds = ParsePositiveInt(ValueAfter(parts, "--timeout-seconds"), 1800, "--timeout-seconds");
        var receiptsDirectory = ValueAfter(parts, "--receipts") ?? defaultReceiptsDirectory;
        var harnesses = spec.Harnesses?.Select(harness => new TrialHarnessSpec(
            harness.Name ?? string.Empty,
            harness.FileName ?? string.Empty,
            harness.Arguments ?? [],
            harness.Environment)).ToArray() ?? [];

        return new TrialComparisonRequest(
            spec.SourceRepositoryPath ?? string.Empty,
            spec.BaseCommit ?? string.Empty,
            harnesses,
            Path.GetFullPath(receiptsDirectory),
            spec.TrialBaseDirectory,
            spec.ProtectedPaths,
            TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static string? ValueAfter(IReadOnlyList<string> parts, string flag)
    {
        for (var index = 1; index < parts.Count; index++)
        {
            if (!parts[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= parts.Count || parts[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{flag} requires a value.");
            }

            return parts[index + 1];
        }

        return null;
    }

    private static int ParsePositiveInt(string? value, int fallback, string flag)
    {
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{flag} must be a positive integer.");
        }

        return parsed;
    }

    private sealed record TrialCompareSpec(
        string? SourceRepositoryPath,
        string? BaseCommit,
        IReadOnlyList<TrialCompareHarnessSpec>? Harnesses,
        IReadOnlyList<string>? ProtectedPaths,
        string? TrialBaseDirectory);

    private sealed record TrialCompareHarnessSpec(
        string? Name,
        string? FileName,
        IReadOnlyList<string>? Arguments,
        IReadOnlyDictionary<string, string?>? Environment);
}
