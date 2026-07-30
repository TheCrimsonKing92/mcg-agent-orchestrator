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
    internal const int MaxPlanChars = 40_000;
    internal const int MinimumPlanChars = 800;
    private const int CapturedOutputTailChars = MaxPlanChars + 8_000;

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
        string workingDirectory)
    {
        var captured = $"{standardOutput}{Environment.NewLine}{standardError}";
        if (TryValidate(captured, out var plan, out var diagnostic) &&
            ValidateCitedPaths(plan, workingDirectory, out diagnostic))
        {
            return new PlannerOutputContractResult(true, plan, null, string.Empty);
        }

        var pathFailures = new List<string>();
        foreach (var path in FindCandidatePlanPaths(captured, workingDirectory))
        {
            if (!TryReadBoundedPlan(path, out var externalPlan, out var readFailure))
            {
                pathFailures.Add($"{path}: {readFailure}");
                continue;
            }

            if (TryValidate(externalPlan, out plan, out var externalDiagnostic) &&
                ValidateCitedPaths(plan, workingDirectory, out externalDiagnostic))
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
            if (stream.Length > CapturedOutputTailChars)
            {
                stream.Seek(-CapturedOutputTailChars, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"[captured Planner output unreadable: {error.Message}]";
        }
    }

    internal static bool TryAppendIngestedReceipt(
        string standardOutputPath,
        string sourcePath,
        string plan,
        out string diagnostic)
    {
        try
        {
            File.AppendAllText(
                standardOutputPath,
                BuildIngestedReceipt(sourcePath, plan),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostic = $"could not append ingested Planner plan to captured stdout: {error.Message}";
            return false;
        }
    }

    internal static bool TryValidate(string text, out string plan, out string diagnostic)
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

        sections.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var end = index + 1 < sections.Count ? sections[index + 1].Start : FindPlanEnd(normalized, section.BodyStart);
            var body = normalized[section.BodyStart..end].Trim();
            if (body.Length < 40)
            {
                diagnostic = $"required section '{section.Label}' is not substantive";
                return false;
            }

            if (!HasRequiredSectionEvidence(section.Label, body))
            {
                diagnostic = $"required section '{section.Label}' lacks its mechanical evidence marker";
                return false;
            }
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

    private static bool ValidateCitedPaths(string plan, string workingDirectory, out string diagnostic)
    {
        diagnostic = string.Empty;
        var targetHeading = TargetSeamsHeading().Match(plan);
        if (!targetHeading.Success)
        {
            diagnostic = "target seam section could not be located for citation validation";
            return false;
        }

        var nextHeading = MarkdownHeading().Match(plan, targetHeading.Index + targetHeading.Length);
        var sectionEnd = nextHeading.Success ? nextHeading.Index : plan.Length;
        var targetSection = plan[targetHeading.Index..sectionEnd];
        foreach (Match match in BacktickedCitation().Matches(targetSection))
        {
            var citation = match.Groups["citation"].Value.Trim();
            var citedPath = NormalizeCitedPath(citation);
            if (citedPath is null)
            {
                continue;
            }

            var prefixStart = Math.Max(0, match.Index - 32);
            var prefix = targetSection[prefixStart..match.Index];
            if (prefix.Contains("new file", StringComparison.OrdinalIgnoreCase) ||
                prefix.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                prefix.Contains("add", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = Path.IsPathFullyQualified(citedPath)
                ? citedPath
                : Path.Combine(workingDirectory, citedPath.Replace('/', Path.DirectorySeparatorChar));
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
                body.Contains("valid", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("invalid", StringComparison.OrdinalIgnoreCase),
            "acceptance criterion mapping" =>
                body.Contains("map", StringComparison.OrdinalIgnoreCase),
            "target seams and symbols" =>
                body.Contains('`') &&
                TargetCitation().IsMatch(body),
            "ownership and lifecycle" =>
                body.Contains("own", StringComparison.OrdinalIgnoreCase),
            "integration seams" =>
                IntegrationSequenceMarker().IsMatch(body),
            "verification commands and classes" =>
                body.Contains('`') &&
                (body.Contains("TEST-VERIFIABLE", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("REAL-WORLD-DEPENDENT", StringComparison.OrdinalIgnoreCase)),
            "risks and stop conditions" =>
                body.Contains("stop", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    internal static string BuildIngestedReceipt(string path, string plan) =>
        $"{Environment.NewLine}{Environment.NewLine}" +
        $"## Durable Planner Plan (ingested by orchestrator from {path}){Environment.NewLine}" +
        plan +
        Environment.NewLine;

    private static int FindPlanEnd(string text, int afterLastHeading)
    {
        var workerResult = text.IndexOf("\nWORKER_RESULT:", afterLastHeading, StringComparison.OrdinalIgnoreCase);
        return workerResult >= 0 ? workerResult : text.Length;
    }

    private static IReadOnlyList<string> FindCandidatePlanPaths(string text, string workingDirectory)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AbsoluteTextPath().Matches(text))
        {
            var raw = match.Groups["path"].Value.Trim().TrimEnd('.', ',', ';', ':', ')', ']');
            if (!Path.IsPathFullyQualified(raw))
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

            if (IsAllowedPlanPath(fullPath, workingDirectory))
            {
                candidates.Add(fullPath);
            }
        }

        return candidates.ToArray();
    }

    private static bool IsAllowedPlanPath(string path, string workingDirectory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(workingDirectory), path);
        if (!relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return true;
        }

        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/.claude/plans/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.codex/plans/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadBoundedPlan(string path, out string text, out string diagnostic)
    {
        text = string.Empty;
        diagnostic = string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxPlanChars)
            {
                diagnostic = $"artifact is {stream.Length} bytes; maximum durable size is {MaxPlanChars}";
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

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+integration[ \t]+seams?[ \t]*$")]
    private static partial Regex IntegrationSeamsHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+verification[ \t]+commands?[ \t]+and[ \t]+classes[ \t]*$")]
    private static partial Regex VerificationHeading();

    [GeneratedRegex(@"(?im)^[ \t]{0,3}#{1,6}[ \t]+risks?[ \t]+and[ \t]+stop[ \t]+conditions?[ \t]*$")]
    private static partial Regex RisksHeading();

    [GeneratedRegex(@"(?im)(?:[`""](?<path>(?:[A-Z]:\\|/)[^`""\r\n]+?\.(?:md|txt))[`""]|(?<path>[A-Z]:\\[^\r\n`""<>|?*]+?\.(?:md|txt)))")]
    private static partial Regex AbsoluteTextPath();

    [GeneratedRegex(@"(?i)(?:src[/\\]|tests[/\\]|\.cs\b|`[A-Za-z_][A-Za-z0-9_.]+`)")]
    private static partial Regex TargetCitation();

    [GeneratedRegex(@"(?i)\b(?:before|after|between|into|from|then|sequence)\b")]
    private static partial Regex IntegrationSequenceMarker();

    [GeneratedRegex(@"`(?<citation>[^`\r\n]+)`")]
    private static partial Regex BacktickedCitation();

    [GeneratedRegex(@"(?i)^(?<path>.+?\.(?:cs|csproj|ps1|md|json|yml|yaml|props|targets|txt))(?:(?::\d+)|(?:#L\d+)|(?:::.+))?$")]
    private static partial Regex CitedFilePath();

    [GeneratedRegex(@"(?m)^[ \t]{0,3}#{1,6}[ \t]+")]
    private static partial Regex MarkdownHeading();
}
