using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record PlannerOutputContractResult(
    bool Succeeded,
    string? Plan,
    string? IngestedPath,
    string Diagnostic);

internal static partial class PlannerOutputContract
{
    internal const int MaxPlanChars = 256_000;
    internal const int MinimumPlanChars = 800;
    internal const string DurablePlanBeginMarker = "<!-- MCG_DURABLE_PLANNER_PLAN:BEGIN -->";
    internal const string DurablePlanEndMarker = "<!-- MCG_DURABLE_PLANNER_PLAN:END -->";
    private const int CapturedOutputTailBytes = (MaxPlanChars * 4) + 32_000;
    private const int AppendAttempts = 4;

    private static readonly (string Label, Regex Heading)[] RequiredSections =
    [
        ("premise validity", PremiseValidityHeading()),
        ("acceptance criterion mapping", AcceptanceMappingHeading()),
        ("target seams and symbols", TargetSeamsHeading()),
        ("ownership and lifecycle", OwnershipLifecycleHeading()),
        ("integration seams", IntegrationSeamsHeading()),
        ("verification commands and classes", VerificationHeading()),
        ("risks and stop conditions", RisksHeading())
    ];

    internal static PlannerOutputContractResult Resolve(
        string standardOutput,
        string standardError,
        string workingDirectory,
        string? modelHomeDirectory = null,
        IReadOnlyList<string>? acceptanceCriteria = null)
    {
        // Planner plans are an stdout contract. Stderr can contain tool traces or echoed
        // file contents and must not make an otherwise incomplete Planner result pass.
        var captured = standardOutput;
        if (TryExtractDurablePlan(captured, out var plan, out _))
        {
            if (TryValidatePlan(
                    plan,
                    workingDirectory,
                    out var revalidatedPlan,
                    out var receiptDiagnostic,
                    acceptanceCriteria))
            {
                // Reconciliation reuses the existing receipt without treating its headings
                // as fresh Planner output or appending another copy.
                return new PlannerOutputContractResult(true, revalidatedPlan, null, string.Empty);
            }

            return new PlannerOutputContractResult(
                false,
                null,
                null,
                $"Planner durable receipt failed revalidation: {receiptDiagnostic}. Retry Planner for contract repair.");
        }

        if (TryValidatePlan(captured, workingDirectory, out plan, out var diagnostic, acceptanceCriteria))
        {
            return new PlannerOutputContractResult(true, plan, null, string.Empty);
        }

        var pathFailures = new List<string>();
        foreach (var path in FindCandidatePlanPaths(captured, workingDirectory, modelHomeDirectory))
        {
            if (!TryReadBoundedPlan(path, out var externalPlan, out var readFailure))
            {
                pathFailures.Add($"{path}: {readFailure}");
                continue;
            }

            if (TryValidatePlan(externalPlan, workingDirectory, out plan, out var externalDiagnostic, acceptanceCriteria))
            {
                return new PlannerOutputContractResult(true, plan, path, string.Empty);
            }

            pathFailures.Add($"{path}: {externalDiagnostic}");
        }

        var pathDetail = pathFailures.Count == 0
            ? "no readable orchestrator-workspace or model-home plan artifact was referenced"
            : string.Join("; ", pathFailures);
        return new PlannerOutputContractResult(
            false,
            null,
            null,
            $"Planner output contract failed: {diagnostic}; {pathDetail}. Retry Planner for contract repair.");
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
            return $"[captured Planner output unreadable: {error.Message}]";
        }
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

    internal static bool TryPersistDurableReceipt(
        string standardOutputPath,
        string sourcePath,
        string plan,
        out string diagnostic)
    {
        var normalizedPlan = plan.ReplaceLineEndings("\n");
        var receipt = BuildIngestedReceipt(sourcePath, normalizedPlan);
        var existingTail = ReadCapturedOutputTail(standardOutputPath);
        if (TryExtractDurablePlan(existingTail, out var existingPlan, out _) &&
            string.Equals(existingPlan.ReplaceLineEndings("\n"), normalizedPlan, StringComparison.Ordinal))
        {
            diagnostic = string.Empty;
            return true;
        }

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
                    diagnostic = $"could not append durable Planner plan to captured stdout: {error.Message}";
                    return false;
                }

                Thread.Sleep(20 * attempt);
            }
        }

        diagnostic = "could not append durable Planner plan to captured stdout";
        return false;
    }

    internal static bool TryExtractDurablePlan(string text, out string plan, out string diagnostic)
    {
        plan = string.Empty;
        diagnostic = string.Empty;
        var begin = text.LastIndexOf(DurablePlanBeginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            diagnostic = "durable Planner plan begin marker is missing";
            return false;
        }

        var contentStart = begin + DurablePlanBeginMarker.Length;
        var end = text.IndexOf(DurablePlanEndMarker, contentStart, StringComparison.Ordinal);
        if (end < 0)
        {
            diagnostic = "durable Planner plan end marker is missing";
            return false;
        }

        plan = text[contentStart..end].Trim();
        if (plan.Length == 0)
        {
            diagnostic = "durable Planner plan is empty";
            return false;
        }

        if (plan.Length > MaxPlanChars)
        {
            diagnostic = $"durable Planner plan is {plan.Length} characters; maximum is {MaxPlanChars}";
            plan = string.Empty;
            return false;
        }

        return true;
    }

    internal static bool TryValidate(
        string text,
        out string plan,
        out string diagnostic,
        IReadOnlyList<string>? acceptanceCriteria = null)
    {
        plan = string.Empty;
        diagnostic = string.Empty;
        var normalized = text.ReplaceLineEndings("\n");
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

        if (acceptanceCriteria is not null)
        {
            var externalHeading = ExternalEdgeContractsHeading().Match(normalized);
            if (!externalHeading.Success)
            {
                diagnostic = "missing required section 'external and edge contracts'";
                return false;
            }

            sections.Add((
                "external and edge contracts",
                externalHeading.Index,
                externalHeading.Index + externalHeading.Length));
        }

        sections.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var end = index + 1 < sections.Count ? sections[index + 1].Start : FindPlanEnd(normalized, section.BodyStart);
            var body = normalized[section.BodyStart..end].Trim();
            var heading = normalized[section.Start..section.BodyStart].Trim();
            if (body.Length < 40)
            {
                diagnostic = BuildSectionFailureDiagnostic(
                    section.Label,
                    heading,
                    body,
                    "must contain at least 40 characters of substantive content in its section body");
                return false;
            }

            if (section.Label != "acceptance criterion mapping" &&
                !HasRequiredSectionEvidence(section.Label, body))
            {
                diagnostic = BuildSectionFailureDiagnostic(
                    section.Label,
                    heading,
                    body,
                    RequiredSectionEvidenceRequirement(section.Label));
                return false;
            }
        }

        if (acceptanceCriteria is { Count: > 0 } &&
            !TryValidateCriterionMappings(normalized, sections, acceptanceCriteria.Count, out diagnostic))
        {
            return false;
        }

        var planStart = sections[0].Start;
        var planEnd = FindPlanEnd(normalized, sections[^1].BodyStart);
        plan = normalized[planStart..planEnd].Trim();
        if (plan.Length < MinimumPlanChars)
        {
            diagnostic = $"complete plan is only {plan.Length} characters; minimum is {MinimumPlanChars}";
            plan = string.Empty;
            return false;
        }

        if (plan.Length > MaxPlanChars)
        {
            diagnostic = $"complete plan is {plan.Length} characters; maximum durable size is {MaxPlanChars}";
            plan = string.Empty;
            return false;
        }

        return true;
    }

    internal static bool TryValidatePlan(
        string text,
        string workingDirectory,
        out string plan,
        out string diagnostic,
        IReadOnlyList<string>? acceptanceCriteria = null) =>
        TryValidate(text, out plan, out diagnostic, acceptanceCriteria) &&
        ValidateCitedPaths(plan, workingDirectory, out diagnostic);

    private static bool TryValidateCriterionMappings(
        string normalized,
        IReadOnlyList<(string Label, int Start, int BodyStart)> sections,
        int criterionCount,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        var mappingIndex = sections.ToList().FindIndex(section => section.Label == "acceptance criterion mapping");
        var mapping = sections[mappingIndex];
        var end = mappingIndex + 1 < sections.Count
            ? sections[mappingIndex + 1].Start
            : FindPlanEnd(normalized, mapping.BodyStart);
        var body = normalized[mapping.BodyStart..end];
        for (var criterion = 1; criterion <= criterionCount; criterion++)
        {
            if (!Regex.IsMatch(
                    body,
                    $@"(?im)^[ \t]*(?:[-*][ \t]+)?(?:criterion[ \t]+)?{criterion}(?:[.)\]:-]|[ \t]+(?:maps?|→|=>))"))
            {
                diagnostic = $"acceptance criterion mapping is incomplete: criterion {criterion} is unmapped";
                return false;
            }
        }

        return true;
    }

    private static bool ValidateCitedPaths(string plan, string workingDirectory, out string diagnostic)
    {
        diagnostic = string.Empty;
        string? contextualDirectory = null;
        var targetHeading = TargetSeamsHeading().Match(plan);
        if (!targetHeading.Success)
        {
            diagnostic = "target seam section could not be located for citation validation";
            return false;
        }

        var nextHeading = MarkdownHeading().Match(plan, targetHeading.Index + targetHeading.Length);
        var sectionEnd = nextHeading.Success ? nextHeading.Index : plan.Length;
        var targetSection = plan[targetHeading.Index..sectionEnd];
        var precedingCitations = CollectExplicitCitations(
            plan[..targetHeading.Index],
            workingDirectory);
        foreach (Match match in BacktickedCitation().Matches(targetSection))
        {
            var citation = match.Groups["citation"].Value.Trim();
            var citedPath = NormalizeCitedPath(citation);
            if (citedPath is null)
            {
                continue;
            }

            var hasExplicitDirectory = citedPath.Contains('/') || citedPath.Contains('\\');
            var candidate = Path.IsPathFullyQualified(citedPath)
                ? citedPath
                : hasExplicitDirectory || contextualDirectory is null
                    ? Path.Combine(workingDirectory, citedPath.Replace('/', Path.DirectorySeparatorChar))
                    : Path.Combine(contextualDirectory, citedPath);

            var inheritedNewFileMarker = false;
            if (!hasExplicitDirectory &&
                !File.Exists(candidate) &&
                !Directory.Exists(candidate) &&
                precedingCitations.TryGetValue(Path.GetFileName(citedPath), out var precedingMatches))
            {
                var distinctMatches = precedingMatches
                    .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (distinctMatches.Length == 1)
                {
                    candidate = distinctMatches[0].Path;
                    inheritedNewFileMarker = distinctMatches[0].IsNewFile;
                }
            }

            if (hasExplicitDirectory)
            {
                contextualDirectory = Directory.Exists(candidate) ||
                    citedPath.EndsWith('/') ||
                    citedPath.EndsWith('\\')
                        ? candidate
                        : Path.GetDirectoryName(candidate);
            }

            inheritedNewFileMarker = inheritedNewFileMarker ||
                precedingCitations.TryGetValue(Path.GetFileName(candidate), out var priorMatches) &&
                priorMatches.Any(item =>
                    item.IsNewFile &&
                    string.Equals(item.Path, candidate, StringComparison.OrdinalIgnoreCase));

            if (inheritedNewFileMarker || IsMarkedAsNewFile(targetSection, match))
            {
                continue;
            }

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                diagnostic = $"target citation '{citation}' does not exist and is not marked as a new file";
                return false;
            }
        }

        return true;
    }

    private static string? NormalizeCitedPath(string citation)
    {
        if (citation.Contains('(') || citation.Contains(')'))
        {
            return null;
        }

        var fileMatch = CitedFilePath().Match(citation);
        if (fileMatch.Success)
        {
            return fileMatch.Groups["path"].Value;
        }

        return citation.Contains('/') || citation.Contains('\\')
            ? citation
            : null;
    }

    private static bool HasRequiredSectionEvidence(string label, string body)
    {
        return label switch
        {
            "premise validity" =>
                PremiseValidityMarker().IsMatch(body),
            "target seams and symbols" =>
                body.Contains('`') &&
                TargetCitation().IsMatch(body),
            "ownership and lifecycle" =>
                OwnershipMarker().IsMatch(body),
            "external and edge contracts" =>
                ExternalEdgeMarker().IsMatch(body),
            "integration seams" =>
                IntegrationSequenceMarker().IsMatch(body) ||
                HasSubstantivelyOrderedNumberedList(body),
            "verification commands and classes" =>
                body.Contains('`') &&
                (body.Contains("TEST-VERIFIABLE", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("REAL-WORLD-DEPENDENT", StringComparison.OrdinalIgnoreCase)),
            "risks and stop conditions" =>
                StopConditionMarker().IsMatch(body),
            _ => false
        };
    }

    private static bool HasSubstantivelyOrderedNumberedList(string body)
    {
        var items = NumberedIntegrationItem().Matches(body);
        if (items.Count < 2)
        {
            return false;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!int.TryParse(item.Groups["number"].Value, out var number) ||
                number != index + 1)
            {
                return false;
            }

            var content = item.Groups["content"].Value.Trim();
            if (content.Length < 12 ||
                !content.Any(char.IsLetter) ||
                IntegrationPlaceholderMarker().IsMatch(content))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, List<(string Path, bool IsNewFile)>> CollectExplicitCitations(
        string text,
        string workingDirectory)
    {
        var citations = new Dictionary<string, List<(string Path, bool IsNewFile)>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in BacktickedCitation().Matches(text))
        {
            var citedPath = NormalizeCitedPath(match.Groups["citation"].Value.Trim());
            if (citedPath is null ||
                (!citedPath.Contains('/') && !citedPath.Contains('\\')))
            {
                continue;
            }

            var candidate = Path.IsPathFullyQualified(citedPath)
                ? citedPath
                : Path.Combine(workingDirectory, citedPath.Replace('/', Path.DirectorySeparatorChar));
            var fileName = Path.GetFileName(candidate);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            if (!citations.TryGetValue(fileName, out var matches))
            {
                matches = [];
                citations.Add(fileName, matches);
            }

            matches.Add((candidate, IsMarkedAsNewFile(text, match)));
        }

        return citations;
    }

    private static bool IsMarkedAsNewFile(string text, Match match)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var lineEnd = text.IndexOf('\n', match.Index + match.Length);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        var prefix = text[lineStart..match.Index];
        var suffix = text[(match.Index + match.Length)..lineEnd];
        return NewFileCitationPrefix().IsMatch(prefix) || NewFileCitationSuffix().IsMatch(suffix);
    }

    private static string RequiredSectionEvidenceRequirement(string label) =>
        label switch
        {
            "premise validity" =>
                "must explicitly state whether the premise is valid or invalid in its section body",
            "target seams and symbols" =>
                "must cite a concrete target seam or symbol in backticks in its section body",
            "ownership and lifecycle" =>
                "must state ownership or lifecycle responsibility in its section body",
            "external and edge contracts" =>
                "must describe external interaction or edge-case behavior in its section body",
            "integration seams" =>
                "must describe an integration sequence in its section body",
            "verification commands and classes" =>
                "must include a backticked verification command or class and identify its verification class in its section body",
            "risks and stop conditions" =>
                "must state a stop condition in its section body",
            _ => "must contain the required evidence in its section body"
        };

    private static string BuildSectionFailureDiagnostic(
        string label,
        string heading,
        string body,
        string requirement)
    {
        var normalizedBody = body.ReplaceLineEndings("\n");
        var lines = normalizedBody.Split('\n');
        var excerpt = string.Join('\n', lines.Take(5));
        var ellipsized = lines.Length > 5;
        if (excerpt.Length > 200)
        {
            excerpt = excerpt[..200];
            ellipsized = true;
        }

        excerpt = excerpt.Length == 0
            ? "(empty)"
            : excerpt
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
        if (ellipsized)
        {
            excerpt += "…";
        }

        return $"required section '{label}' {requirement}; inspected heading '{heading}'; inspected section-body excerpt: \"{excerpt}\"";
    }

    internal static string BuildIngestedReceipt(string path, string plan) =>
        $"{Environment.NewLine}{Environment.NewLine}" +
        $"## Durable Planner Plan (ingested by orchestrator from {path}){Environment.NewLine}" +
        DurablePlanBeginMarker +
        Environment.NewLine +
        plan.ReplaceLineEndings("\n") +
        Environment.NewLine +
        DurablePlanEndMarker +
        Environment.NewLine;

    private static int FindPlanEnd(string text, int afterLastHeading)
    {
        var workerResult = text.IndexOf("\nWORKER_RESULT:", afterLastHeading, StringComparison.OrdinalIgnoreCase);
        var durableReceiptEnd = text.IndexOf(
            $"\n{DurablePlanEndMarker}",
            afterLastHeading,
            StringComparison.Ordinal);
        if (workerResult < 0)
        {
            return durableReceiptEnd >= 0 ? durableReceiptEnd : text.Length;
        }

        return durableReceiptEnd >= 0
            ? Math.Min(workerResult, durableReceiptEnd)
            : workerResult;
    }

    private static IReadOnlyList<string> FindCandidatePlanPaths(
        string text,
        string workingDirectory,
        string? modelHomeDirectory)
    {
        var candidates = new List<(int Index, string Path)>();
        foreach (Match match in AbsoluteTextPath().Matches(text))
        {
            var raw = match.Groups["path"].Value.Trim().TrimEnd('.', ',', ';', ':', ')', ']');
            if (!Path.IsPathFullyQualified(raw))
            {
                continue;
            }

            var lineStart = text.LastIndexOfAny(['\r', '\n'], Math.Max(0, match.Index - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            var lineEnd = text.IndexOfAny(['\r', '\n'], match.Index + match.Length);
            lineEnd = lineEnd < 0 ? text.Length : lineEnd;
            var referenceLine = text[lineStart..lineEnd];
            if (!ExternalPlanReference().IsMatch(referenceLine))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(raw);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (IsAllowedPlanPath(fullPath, workingDirectory, modelHomeDirectory))
            {
                candidates.Add((match.Index, fullPath));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Index)
            .Select(candidate => candidate.Path)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsAllowedPlanPath(
        string path,
        string workingDirectory,
        string? modelHomeDirectory)
    {
        if (IsContainedPath(path, workingDirectory))
        {
            return true;
        }

        var home = string.IsNullOrWhiteSpace(modelHomeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : modelHomeDirectory;
        return !string.IsNullOrWhiteSpace(home) &&
            (IsContainedPath(path, Path.Combine(home, ".claude", "plans")) ||
             IsContainedPath(path, Path.Combine(home, ".codex", "plans")));
    }

    private static bool IsContainedPath(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(fullPath, fullRoot, comparison) ||
                fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception error) when (
            error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryReadBoundedPlan(string path, out string text, out string diagnostic)
    {
        text = string.Empty;
        diagnostic = string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > (MaxPlanChars * 4L) + 4)
            {
                diagnostic = $"artifact is {stream.Length} bytes; maximum bounded UTF-8 size is {(MaxPlanChars * 4L) + 4}";
                return false;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxPlanChars + 1];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            if (read > MaxPlanChars || reader.Peek() >= 0)
            {
                diagnostic = $"artifact exceeds maximum durable size {MaxPlanChars}";
                return false;
            }

            text = new string(buffer, 0, read);
            return true;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostic = error.Message;
            return false;
        }
    }

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+premise[ \t]+validity[ \t]*$")]
    private static partial Regex PremiseValidityHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+acceptance[ \t]+(?:criterion|criteria)[ \t]+mapping[ \t]*$")]
    private static partial Regex AcceptanceMappingHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+target[ \t]+seams?[ \t]+and[ \t]+symbols?[ \t]*$")]
    private static partial Regex TargetSeamsHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+ownership[ \t]+and[ \t]+lifecycle[ \t]*$")]
    private static partial Regex OwnershipLifecycleHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+external[ \t]+and[ \t]+edge[ \t]+contracts?[ \t]*$")]
    private static partial Regex ExternalEdgeContractsHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+integration[ \t]+seams?[ \t]*$")]
    private static partial Regex IntegrationSeamsHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+verification[ \t]+commands?[ \t]+and[ \t]+classes[ \t]*$")]
    private static partial Regex VerificationHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+risks?[ \t]+and[ \t]+stop[ \t]+conditions?[ \t]*$")]
    private static partial Regex RisksHeading();

    [GeneratedRegex(@"(?im)(?:[`""](?<path>(?:[A-Z]:\\|/)[^`""\r\n]+?\.(?:md|txt))[`""]|(?<path>[A-Z]:\\[^\r\n`""<>|?*]+?\.(?:md|txt)))")]
    private static partial Regex AbsoluteTextPath();

    [GeneratedRegex(@"(?i)(?:\b(?:plan|planning)\b.{0,120}\b(?:written|saved|created|available|located|file|path)\b|\b(?:written|saved|created|available|located|file|path)\b.{0,120}\b(?:plan|planning)\b)")]
    private static partial Regex ExternalPlanReference();

    [GeneratedRegex(@"(?i)(?:src[/\\]|tests[/\\]|\.cs\b|`[A-Za-z_][A-Za-z0-9_.]+`)")]
    private static partial Regex TargetCitation();

    [GeneratedRegex(@"(?i)\b(?:before|after|between|into|from|then|sequence)\b")]
    private static partial Regex IntegrationSequenceMarker();

    [GeneratedRegex(@"(?m)^[ \t]*(?<number>\d+)[.)][ \t]+(?<content>\S[^\r\n]*)$")]
    private static partial Regex NumberedIntegrationItem();

    [GeneratedRegex(@"(?i)\b(?:tbd|todo|placeholder|later)\b")]
    private static partial Regex IntegrationPlaceholderMarker();

    [GeneratedRegex(@"(?i)\b(?:valid|invalid)\b")]
    private static partial Regex PremiseValidityMarker();

    [GeneratedRegex(@"(?i)\b(?:own|owns|owned|ownership)\b")]
    private static partial Regex OwnershipMarker();

    [GeneratedRegex(@"(?i)\b(?:failure|edge|external|unhappy|invalid|missing|oversized|timeout)\b")]
    private static partial Regex ExternalEdgeMarker();

    [GeneratedRegex(@"(?i)\bstop(?:s|ped|ping)?\b")]
    private static partial Regex StopConditionMarker();

    [GeneratedRegex(@"(?i)(?:\bnew[ \t]+file\b|\b(?:create|add)\b(?:[ \t]+(?:a|an|the|new))?)[^`\r\n]{0,24}$")]
    private static partial Regex NewFileCitationPrefix();

    [GeneratedRegex(@"(?i)^[ \t]*(?:—[ \t]*new[ \t]+file\b|\([ \t]*new[ \t]+file[ \t]*\))")]
    private static partial Regex NewFileCitationSuffix();

    [GeneratedRegex(@"`(?<citation>[^`\r\n]+)`")]
    private static partial Regex BacktickedCitation();

    [GeneratedRegex(@"(?i)^(?<path>.+?\.(?:cs|csproj|ps1|md|json|yml|yaml|props|targets|txt))(?:(?::\d+)|(?:#L\d+)|(?:::.+))?$")]
    private static partial Regex CitedFilePath();

    [GeneratedRegex(@"(?m)^[ \t]{0,3}#{1,6}[ \t]+")]
    private static partial Regex MarkdownHeading();
}
