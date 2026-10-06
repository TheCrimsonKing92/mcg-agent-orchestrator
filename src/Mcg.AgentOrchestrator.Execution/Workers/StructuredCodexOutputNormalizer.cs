using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record StructuredCodexNormalizationResult(
    CodexJsonlParseResult? Parsed,
    PlannerCandidateNormalizationState State);

internal static class StructuredCodexOutputNormalizer
{
    internal static StructuredCodexNormalizationResult Normalize(
        TaskDispatchRecord? dispatch,
        string standardOutputPath,
        Func<string, string>? readAllText = null)
    {
        if (dispatch?.WorkerProviderKind is not (ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna) ||
            !dispatch.Command.Contains("--json", StringComparison.OrdinalIgnoreCase))
        {
            return new StructuredCodexNormalizationResult(null, PlannerCandidateNormalizationState.NotRequired);
        }

        var rawAuditPath = standardOutputPath + ".jsonl";
        var hasRawAudit = File.Exists(rawAuditPath);
        if (!hasRawAudit && !File.Exists(standardOutputPath))
        {
            return new StructuredCodexNormalizationResult(
                new CodexJsonlParseResult(string.Empty, null, "unreadable", Recognized: false),
                PlannerCandidateNormalizationState.Unreadable);
        }

        var rawSourcePath = hasRawAudit ? rawAuditPath : standardOutputPath;
        string raw;
        try
        {
            raw = (readAllText ?? File.ReadAllText)(rawSourcePath);
        }
        catch (IOException)
        {
            return Unreadable();
        }
        catch (UnauthorizedAccessException)
        {
            return Unreadable();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new StructuredCodexNormalizationResult(
                new CodexJsonlParseResult(string.Empty, null, "absent", Recognized: false),
                PlannerCandidateNormalizationState.Empty);
        }

        var parsed = CodexJsonlUsageParser.Parse(raw);
        if (!parsed.Recognized)
        {
            return new StructuredCodexNormalizationResult(
                parsed,
                parsed.MalformedLineCount > 0
                    ? PlannerCandidateNormalizationState.Malformed
                    : PlannerCandidateNormalizationState.Unrecognized);
        }

        if (string.IsNullOrWhiteSpace(parsed.WorkerOutput))
        {
            return new StructuredCodexNormalizationResult(
                parsed,
                parsed.MalformedLineCount > 0
                    ? PlannerCandidateNormalizationState.Malformed
                    : PlannerCandidateNormalizationState.Empty);
        }

        try
        {
            if (!File.Exists(rawAuditPath))
                File.Copy(standardOutputPath, rawAuditPath, overwrite: false);

            File.WriteAllText(standardOutputPath, parsed.WorkerOutput, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // The raw process log remains authoritative when normalization cannot replace it.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the raw process log and retain the typed parsed result.
        }

        return new StructuredCodexNormalizationResult(
            parsed,
            parsed.MalformedLineCount > 0
                ? PlannerCandidateNormalizationState.Malformed
                : PlannerCandidateNormalizationState.Normalized);
    }

    private static StructuredCodexNormalizationResult Unreadable() => new(
        new CodexJsonlParseResult(string.Empty, null, "unreadable", Recognized: false),
        PlannerCandidateNormalizationState.Unreadable);
}
