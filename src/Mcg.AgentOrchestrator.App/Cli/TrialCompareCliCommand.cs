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
        TextWriter output,
        Func<HistoricalTrialSelector, HistoricalTrialReplayResolution>? historicalResolver = null,
        Func<TrialComparisonRequest, TrialComparisonResult>? comparisonRunner = null)
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

        TrialComparisonRequest request;
        HermesTrialEvidence? hermesEvidence;
        try
        {
            var specJson = File.ReadAllText(fullSpecPath);
            request = ParseSpec(specJson, parts, defaultReceiptsDirectory, historicalResolver);
            hermesEvidence = ParseHermesTrialEvidence(specJson);
        }
        catch (TrialComparisonUnavailableException ex)
        {
            var receipt = WriteUnavailableReceipt(parts, defaultReceiptsDirectory, ex);
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine(JsonSerializer.Serialize(receipt, Json));
            }
            else
            {
                output.WriteLine($"trial-compare succeeded=False preflight={receipt.Reason} receipt={receipt.ReceiptPath}");
            }

            throw new CliExitException(1);
        }

        comparisonRunner ??= candidateRequest => new TrialHarnessComparison(host).Run(candidateRequest);
        var result = comparisonRunner(request);
        HermesTrialEvaluation? hermesEvaluation = null;
        string? hermesEvaluationReceiptPath = null;
        if (hermesEvidence is not null)
        {
            hermesEvaluation = HermesTrialDecisionEngine.EvaluateForComparison(
                result,
                hermesEvidence,
                HermesTrialThresholds.Load(AppContext.BaseDirectory));
            hermesEvaluationReceiptPath = Path.Combine(result.ReceiptDirectory, "hermes-trial-decision.json");
            Directory.CreateDirectory(result.ReceiptDirectory);
            File.WriteAllText(hermesEvaluationReceiptPath, JsonSerializer.Serialize(hermesEvaluation, Json));
        }

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var payload = hermesEvaluation is null
                ? (object)result
                : new TrialCompareEvaluationOutput(result, hermesEvaluation, hermesEvaluationReceiptPath!);
            output.WriteLine(JsonSerializer.Serialize(payload, Json));
        }
        else if (format.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"trial-compare succeeded={result.Succeeded} receipt={result.ReceiptPath}");
            foreach (var harness in result.Harnesses)
            {
                output.WriteLine(
                    $"  {harness.Name}: outcome={harness.Outcome} exit={harness.ExitCode?.ToString() ?? "unavailable"} " +
                    $"workerResult={harness.WorkerResult.Status} stdoutBytes={harness.StandardOutput?.ByteCount.ToString() ?? "unavailable"} " +
                    $"stdoutSha256={harness.StandardOutput?.Sha256 ?? "unavailable"} stderrBytes={harness.StandardError?.ByteCount.ToString() ?? "unavailable"} " +
                    $"stderrSha256={harness.StandardError?.Sha256 ?? "unavailable"}");
            }

            if (hermesEvaluation is not null)
            {
                output.WriteLine(
                    $"hermes-trial disposition={hermesEvaluation.Decision.Disposition} " +
                    $"receipt={hermesEvaluationReceiptPath}");
                foreach (var gate in hermesEvaluation.Gates)
                {
                    output.WriteLine(
                        $"  gate={gate.Gate} status={gate.Status} " +
                        $"reasons={(gate.Reasons.Count == 0 ? "none" : string.Join(",", gate.Reasons))}");
                }
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
        string defaultReceiptsDirectory,
        Func<HistoricalTrialSelector, HistoricalTrialReplayResolution>? historicalResolver = null)
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

        var hasExplicitEvidence = spec.Workload is not null || !string.IsNullOrWhiteSpace(spec.BaseCommit);
        var hasHistoricalSelector = spec.Historical is not null;
        if (hasExplicitEvidence == hasHistoricalSelector)
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.InvalidSourceMode,
                "Trial comparison requires exactly one source mode: explicit baseCommit + workload, or historical selector.");
        }

        string baseCommit;
        TrialWorkload workload;
        if (hasHistoricalSelector)
        {
            if (historicalResolver is null)
            {
                throw Unavailable(
                    TrialComparisonUnavailableReason.HistoricalResolverUnavailable,
                    "Historical replay is unavailable without a read-only durable-state resolver.");
            }

            var resolution = historicalResolver(spec.Historical!);
            baseCommit = resolution.BaseCommit;
            workload = resolution.Workload;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(spec.BaseCommit) || spec.Workload is null)
            {
                throw Unavailable(
                    TrialComparisonUnavailableReason.InvalidExplicitWorkload,
                    "Explicit trial comparison requires both baseCommit and workload evidence.");
            }

            workload = BuildExplicitWorkload(spec.Workload);
            baseCommit = spec.BaseCommit.Trim();
        }

        var collision = harnesses
            .SelectMany(harness => harness.Environment?.Keys.Select(key => (harness.Name, Key: key)) ?? [])
            .FirstOrDefault(pair => pair.Key.StartsWith(TrialIdentity.EnvironmentPrefix, StringComparison.OrdinalIgnoreCase));
        if (collision != default)
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.ReservedEnvironmentCollision,
                $"Harness '{collision.Name}' cannot override reserved environment key '{collision.Key}'.");
        }

        return new TrialComparisonRequest(
            spec.SourceRepositoryPath ?? string.Empty,
            baseCommit,
            workload,
            harnesses,
            Path.GetFullPath(receiptsDirectory),
            spec.TrialBaseDirectory,
            spec.ProtectedPaths,
            TimeSpan.FromSeconds(timeoutSeconds));
    }

    internal static HermesTrialEvidence? ParseHermesTrialEvidence(string json)
    {
        var spec = JsonSerializer.Deserialize<TrialCompareSpec>(json, Json) ??
            throw new ArgumentException("Trial comparison spec was empty.");
        return spec.HermesTrialEvidence;
    }

    internal static bool RequiresHistoricalState(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0 || !parts[0].Equals("trial-compare", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var specPath = ValueAfter(parts, "--spec");
            if (string.IsNullOrWhiteSpace(specPath) || !File.Exists(Path.GetFullPath(specPath)))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(specPath)));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals("historical", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind is not JsonValueKind.Null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            // Invalid specs remain on the state-free path so normal command parsing owns the typed error.
            return false;
        }
    }

    private static TrialWorkload BuildExplicitWorkload(TrialCompareWorkloadSpec workload)
    {
        if (string.IsNullOrWhiteSpace(workload.BriefIdentity)
            || string.IsNullOrWhiteSpace(workload.BriefContent)
            || string.IsNullOrWhiteSpace(workload.BriefDigest)
            || string.IsNullOrWhiteSpace(workload.ModelIdentity)
            || string.IsNullOrWhiteSpace(workload.SourceProvenance))
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.InvalidExplicitWorkload,
                "Explicit workload requires briefIdentity, exact briefContent, briefDigest, modelIdentity, and sourceProvenance.");
        }

        var actualDigest = TrialIdentity.ComputeBriefDigest(workload.BriefContent);
        if (!actualDigest.Equals(workload.BriefDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.InvalidExplicitWorkload,
                $"Explicit workload briefDigest mismatch: expected '{workload.BriefDigest}', actual '{actualDigest}'.");
        }

        return new TrialWorkload(
            workload.BriefIdentity.Trim(),
            workload.BriefContent,
            actualDigest,
            workload.ModelIdentity.Trim(),
            workload.SourceProvenance.Trim());
    }

    private static TrialComparisonPreflightReceipt WriteUnavailableReceipt(
        IReadOnlyList<string> parts,
        string defaultReceiptsDirectory,
        TrialComparisonUnavailableException exception)
    {
        var receiptDirectory = Path.Combine(
            Path.GetFullPath(ValueAfter(parts, "--receipts") ?? defaultReceiptsDirectory),
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiptDirectory);
        var receiptPath = Path.Combine(receiptDirectory, "preflight.json");
        var receipt = new TrialComparisonPreflightReceipt(
            Succeeded: false,
            exception.Reason.ToString(),
            exception.Message,
            receiptPath,
            DateTimeOffset.UtcNow);
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt, Json));
        return receipt;
    }

    private static TrialComparisonUnavailableException Unavailable(
        TrialComparisonUnavailableReason reason,
        string message) => new(reason, message);

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
        TrialCompareWorkloadSpec? Workload,
        HistoricalTrialSelector? Historical,
        IReadOnlyList<TrialCompareHarnessSpec>? Harnesses,
        IReadOnlyList<string>? ProtectedPaths,
        string? TrialBaseDirectory,
        HermesTrialEvidence? HermesTrialEvidence);

    private sealed record TrialCompareWorkloadSpec(
        string? BriefIdentity,
        string? BriefContent,
        string? BriefDigest,
        string? ModelIdentity,
        string? SourceProvenance);

    private sealed record TrialCompareHarnessSpec(
        string? Name,
        string? FileName,
        IReadOnlyList<string>? Arguments,
        IReadOnlyDictionary<string, string?>? Environment);

    private sealed record TrialComparisonPreflightReceipt(
        bool Succeeded,
        string Reason,
        string Message,
        string ReceiptPath,
        DateTimeOffset RecordedAt);

    private sealed record TrialCompareEvaluationOutput(
        TrialComparisonResult Comparison,
        HermesTrialEvaluation HermesTrial,
        string HermesTrialReceiptPath);
}
