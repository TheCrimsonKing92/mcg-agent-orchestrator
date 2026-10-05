using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ResearcherOutputContractResult(
    bool Succeeded,
    string? Research,
    string Diagnostic);

internal static partial class ResearcherOutputContract
{
    internal const int MaxResearchChars = 256_000;
    internal const string DurableResearchBeginMarker = "<!-- MCG_DURABLE_RESEARCH:BEGIN -->";
    internal const string DurableResearchEndMarker = "<!-- MCG_DURABLE_RESEARCH:END -->";
    private const int CapturedOutputTailBytes = (MaxResearchChars * 4) + 32_000;
    private const int MinimumResearchChars = 240;
    private const int AppendAttempts = 4;

    private static readonly (string Label, Regex Heading)[] RequiredSections =
    [
        ("current source findings", CurrentSourceHeading()),
        ("prior goal evidence", PriorGoalEvidenceHeading()),
        ("upstream capabilities", UpstreamCapabilitiesHeading()),
        ("likely seams and risks", LikelySeamsHeading())
    ];

    internal static ResearcherOutputContractResult Resolve(string standardOutput)
    {
        if (TryExtractDurableResearch(standardOutput, out var durableResearch, out _))
        {
            return TryValidate(durableResearch, out var revalidated, out var receiptDiagnostic)
                ? new ResearcherOutputContractResult(true, revalidated, string.Empty)
                : new ResearcherOutputContractResult(
                    false,
                    null,
                    $"Researcher durable receipt failed revalidation: {receiptDiagnostic}. Retry Researcher for contract repair.");
        }

        return TryValidate(standardOutput, out var research, out var diagnostic)
            ? new ResearcherOutputContractResult(true, research, string.Empty)
            : new ResearcherOutputContractResult(
                false,
                null,
                $"Researcher output contract failed: {diagnostic}. Retry Researcher for contract repair.");
    }

    internal static bool TryValidate(string text, out string research, out string diagnostic)
    {
        research = string.Empty;
        diagnostic = string.Empty;
        var normalized = MarkdownHeadingNormalizer.SeparateInlineAtxHeadings(text);
        var sections = new List<(string Label, int Start, int BodyStart)>();
        foreach (var (label, heading) in RequiredSections)
        {
            var match = heading.Match(normalized);
            if (!match.Success)
            {
                diagnostic = $"missing required section '{label}'";
                return false;
            }

            sections.Add((label, match.Index, match.Index + match.Length));
        }

        sections.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var end = index + 1 < sections.Count
                ? sections[index + 1].Start
                : FindResearchEnd(normalized, section.BodyStart);
            if (normalized[section.BodyStart..end].Trim().Length < 20)
            {
                diagnostic = $"required section '{section.Label}' is not substantive";
                return false;
            }
        }

        var normalizedResearch = normalized[sections[0].Start..FindResearchEnd(normalized, sections[^1].BodyStart)].Trim();
        if (normalizedResearch.Length < MinimumResearchChars)
        {
            diagnostic = $"complete research is only {normalizedResearch.Length} characters; minimum is {MinimumResearchChars}";
            return false;
        }

        if (normalizedResearch.Length > MaxResearchChars)
        {
            diagnostic = $"complete research is {normalizedResearch.Length} characters; maximum durable size is {MaxResearchChars}";
            return false;
        }

        research = normalizedResearch;
        return true;
    }

    internal static string ReadCapturedOutputTail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var startedAtBeginning = true;
            if (stream.Length > CapturedOutputTailBytes)
            {
                stream.Seek(-CapturedOutputTailBytes, SeekOrigin.End);
                startedAtBeginning = false;
                SkipUtf8ContinuationBytes(stream);
            }

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: startedAtBeginning);
            return reader.ReadToEnd();
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"[captured Researcher output unreadable: {error.Message}]";
        }
    }

    internal static bool TryPersistDurableReceipt(
        string standardOutputPath,
        string research,
        out string diagnostic)
    {
        var normalizedResearch = research.ReplaceLineEndings("\n");
        var existingTail = ReadCapturedOutputTail(standardOutputPath);
        if (TryExtractDurableResearch(existingTail, out var existingResearch, out _) &&
            string.Equals(existingResearch, normalizedResearch, StringComparison.Ordinal))
        {
            diagnostic = string.Empty;
            return true;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedResearch))).ToLowerInvariant();
        var receipt =
            $"{Environment.NewLine}{Environment.NewLine}" +
            $"## Durable Research Notes (ingested by orchestrator; sha256:{hash}){Environment.NewLine}" +
            DurableResearchBeginMarker + Environment.NewLine +
            normalizedResearch + Environment.NewLine +
            DurableResearchEndMarker + Environment.NewLine;

        for (var attempt = 1; attempt <= AppendAttempts; attempt++)
        {
            try
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(receipt);
                using var stream = new FileStream(
                    standardOutputPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                diagnostic = string.Empty;
                return true;
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                if (attempt == AppendAttempts || error is not IOException)
                {
                    diagnostic = $"could not append durable Researcher artifact to captured stdout: {error.Message}";
                    return false;
                }

                Thread.Sleep(20 * attempt);
            }
        }

        diagnostic = "could not append durable Researcher artifact to captured stdout";
        return false;
    }

    internal static bool TryExtractDurableResearch(string text, out string research, out string diagnostic)
    {
        research = string.Empty;
        diagnostic = string.Empty;
        var begin = text.LastIndexOf(DurableResearchBeginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            diagnostic = "durable Researcher artifact begin marker is missing";
            return false;
        }

        var contentStart = begin + DurableResearchBeginMarker.Length;
        var end = text.IndexOf(DurableResearchEndMarker, contentStart, StringComparison.Ordinal);
        if (end < 0)
        {
            diagnostic = "durable Researcher artifact end marker is missing";
            return false;
        }

        research = text[contentStart..end].Trim();
        if (research.Length == 0 || research.Length > MaxResearchChars)
        {
            diagnostic = research.Length == 0
                ? "durable Researcher artifact is empty"
                : $"durable Researcher artifact is {research.Length} characters; maximum is {MaxResearchChars}";
            research = string.Empty;
            return false;
        }

        return true;
    }

    private static int FindResearchEnd(string text, int afterLastHeading)
    {
        var workerResult = text.IndexOf("\nWORKER_RESULT:", afterLastHeading, StringComparison.OrdinalIgnoreCase);
        var receiptEnd = text.IndexOf($"\n{DurableResearchEndMarker}", afterLastHeading, StringComparison.Ordinal);
        if (workerResult < 0)
        {
            return receiptEnd >= 0 ? receiptEnd : text.Length;
        }

        return receiptEnd >= 0 ? Math.Min(workerResult, receiptEnd) : workerResult;
    }

    private static void SkipUtf8ContinuationBytes(Stream stream)
    {
        while (stream.Position < stream.Length)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                return;
            }

            if ((value & 0b1100_0000) != 0b1000_0000)
            {
                stream.Seek(-1, SeekOrigin.Current);
                return;
            }
        }
    }

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+current[ \t]+source[ \t]+findings?[ \t]*$")]
    private static partial Regex CurrentSourceHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+prior[ \t]+goal[ \t]+evidence[ \t]*$")]
    private static partial Regex PriorGoalEvidenceHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+upstream[ \t]+capabilities[ \t]*$")]
    private static partial Regex UpstreamCapabilitiesHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+likely[ \t]+seams?[ \t]+and[ \t]+risks?[ \t]*$")]
    private static partial Regex LikelySeamsHeading();
}
