using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

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
    private const int MinimumDistinctSectionWords = 8;
    private const int MaxOffendingLineChars = 200;
    private static readonly string[] CandidatePathSuffixes = [".cs"];

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
            ? string.Empty
            : $" Referenced plan artifact failures: {string.Join("; ", pathFailures)}.";
        return new PlannerOutputContractResult(
            false,
            null,
            null,
            $"Planner output contract failed.{pathDetail} Stdout plan reason: {diagnostic}. Retry Planner for contract repair.");
    }

    internal static IReadOnlyDictionary<string, string> SplitRequiredSections(string plan)
    {
        var matches = RequiredSections
            .Select(section => (section.Label, Match: section.Heading.Match(plan)))
            .Where(section => section.Match.Success)
            .OrderBy(section => section.Match.Index)
            .ToArray();
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < matches.Length; index++)
        {
            var bodyStart = matches[index].Match.Index + matches[index].Match.Length;
            var bodyEnd = index + 1 < matches.Length ? matches[index + 1].Match.Index : plan.Length;
            sections[matches[index].Label] = plan[bodyStart..bodyEnd].Trim();
        }

        return sections;
    }

    internal static int FindFirstRequiredHeadingIndex(string text)
    {
        var starts = RequiredSections
            .Select(section => section.Heading.Match(text))
            .Where(match => match.Success)
            .Select(match => match.Index);
        return starts.DefaultIfEmpty(-1).Min();
    }

    internal static PlannerStructuralQualityVector EvaluateStructuralQuality(
        string plan,
        int? criterionCount = null)
    {
        var sections = SplitRequiredSections(plan);
        if (!sections.TryGetValue("acceptance criterion mapping", out var mappingBody))
            return new PlannerStructuralQualityVector(0, 0, 0, 0, 0, 0);

        var completeMappings = 0;
        var concreteOwningSeams = 0;
        var feasibleEvidenceOwners = 0;
        var integrationSeams = 0;
        var verificationClasses = 0;
        var stopConditions = 0;
        var eligibleMappingLines = mappingBody.Split('\n').Where(line =>
            TryParseCriterionMappingLine(line, out var criterion, out var mapping) &&
            criterion > 0 && !string.IsNullOrWhiteSpace(mapping));
        var mappings = SelectCriterionMappings(string.Join("\n", eligibleMappingLines));

        var boundedCriterionCount = criterionCount ?? CountContiguousCriteria(mappings);
        foreach (var mapping in mappings
                     .Where(pair => pair.Key <= boundedCriterionCount)
                     .OrderBy(pair => pair.Key)
                     .Select(pair => pair.Value))
        {

            completeMappings++;
            var codeSpans = BacktickedCitation().Matches(mapping)
                .Select(match => match.Groups["citation"].Value)
                .ToArray();
            if (codeSpans.Any(citation =>
                    citation.Contains('.', StringComparison.Ordinal) ||
                    citation.Contains('/', StringComparison.Ordinal) ||
                    citation.Contains("::", StringComparison.Ordinal)))
            {
                concreteOwningSeams++;
            }

            if (HasFeasibleEvidenceOwner(mapping))
                feasibleEvidenceOwners++;

            if (Regex.IsMatch(mapping, @"(?i)\b(?:integration|seam)\b"))
                integrationSeams++;
            if (Regex.IsMatch(mapping, @"(?i)\b(?:TEST-VERIFIABLE|REAL-WORLD-DEPENDENT)\b"))
                verificationClasses++;
            if (Regex.IsMatch(mapping, @"(?i)\b(?:stop|fail(?:s|ed)? closed|fallback)\b"))
                stopConditions++;
        }

        return new PlannerStructuralQualityVector(
            completeMappings,
            concreteOwningSeams,
            feasibleEvidenceOwners,
            integrationSeams,
            verificationClasses,
            stopConditions);
    }

    private static int CountContiguousCriteria(IReadOnlyDictionary<int, string> mappings)
    {
        var count = 0;
        while (mappings.ContainsKey(count + 1))
            count++;
        return count;
    }

    private static bool HasFeasibleEvidenceOwner(string mapping)
    {
        var testVerifiable = Regex.IsMatch(mapping, @"(?i)\bTEST-VERIFIABLE\b");
        var realWorldDependent = Regex.IsMatch(mapping, @"(?i)\bREAL-WORLD-DEPENDENT\b");
        if (testVerifiable == realWorldDependent)
            return false;

        if (realWorldDependent)
            return DeclaresEvidenceOwner(mapping, "operator");

        if (DeclaresEvidenceOwner(mapping, "Acceptance|conductor"))
            return true;

        return TryGetDeclaredWorkerRole(mapping, out var role) &&
            IsFeasibleWorkerEvidence(mapping, role);
    }

    private static bool TryGetDeclaredWorkerRole(string mapping, out AgentRole role)
    {
        foreach (var candidate in Enum.GetValues<AgentRole>())
        {
            if (DeclaresEvidenceOwner(mapping, Regex.Escape(candidate.ToString())))
            {
                role = candidate;
                return true;
            }
        }

        role = default;
        return false;
    }

    private static bool IsFeasibleWorkerEvidence(
        string mapping,
        AgentRole role)
    {
        if (Regex.IsMatch(
            mapping,
            @"(?i)\b(?:full[-\s]?suite|acceptance[-\s]?gate|test[-\s]?host|gate[-\s]?(?:receipt|wall[-\s]?clock)|coverage[-\s]?total)\b"))
        {
            return false;
        }

        if (Regex.IsMatch(mapping, @"(?i)\bfocused[-\s]?evidence[-\s]?request\b"))
            return DispatchRoleOutputCapabilities.CanProduceEvidence(
                role,
                DispatchRoleEvidenceRequirement.FocusedEvidenceRequest);

        if (Regex.IsMatch(mapping, @"(?i)\b(?:worker[-\s]?build|build[-\s]?(?:result|receipt))\b"))
            return DispatchRoleOutputCapabilities.CanProduceEvidence(
                role,
                DispatchRoleEvidenceRequirement.WorkerBuildResult);

        if (Regex.IsMatch(mapping, @"(?i)\bmanual[-\s]?reproduction\b"))
            return DispatchRoleOutputCapabilities.CanProduceEvidence(
                role,
                DispatchRoleEvidenceRequirement.ManualReproduction);

        if (Regex.IsMatch(mapping, @"(?i)\bsource[-\s]?(?:reproduction|trace)\b"))
            return DispatchRoleOutputCapabilities.CanProduceEvidence(
                role,
                DispatchRoleEvidenceRequirement.SourceTrace);

        if (Regex.IsMatch(mapping, @"(?i)\bverification[-\s]?matrix\b"))
            return DispatchRoleOutputCapabilities.CanProduceEvidence(
                role,
                DispatchRoleEvidenceRequirement.VerificationMatrix);

        return DispatchRoleOutputCapabilities.CanProduceEvidence(
                   role,
                   DispatchRoleEvidenceRequirement.ScopedRepositoryChange) &&
            BacktickedCitation().Matches(mapping).Any(match =>
                match.Groups["citation"].Value.IndexOfAny(['.', '/', '\\']) >= 0 ||
                match.Groups["citation"].Value.Contains("::", StringComparison.Ordinal));
    }

    private static bool DeclaresEvidenceOwner(string mapping, string rolePattern) =>
        Regex.IsMatch(
            mapping,
            $@"(?i)(?:\b(?:{rolePattern})\b(?:\s+|-)(?:owns?|owned)\b|\bowned\s+by\s+(?:{rolePattern})\b)");

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

    internal static bool TryPersistRejectionDiagnostic(
        string standardErrorPath,
        string rejectionDiagnostic,
        out string diagnostic)
    {
        var payload = Environment.NewLine +
            "[orchestrator Planner output contract rejection]" + Environment.NewLine +
            rejectionDiagnostic + Environment.NewLine;
        if (ReadCapturedOutputTail(standardErrorPath).EndsWith(payload, StringComparison.Ordinal))
        {
            diagnostic = string.Empty;
            return true;
        }

        try
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(payload);
            using var stream = new FileStream(
                standardErrorPath,
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
            diagnostic = error.Message;
            return false;
        }
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
                    "must contain at least 40 characters in its section body");
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
        IReadOnlyList<string>? acceptanceCriteria = null)
    {
        if (!TryValidate(text, out plan, out diagnostic, acceptanceCriteria))
        {
            return false;
        }

        if (!TryResolveCitedPaths(plan, workingDirectory, out var resolvedPlan, out diagnostic))
        {
            return false;
        }

        plan = resolvedPlan;
        return true;
    }

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
        var mappingLines = SelectCriterionMappings(body);
        var unparsedLines = new Dictionary<int, string>();
        foreach (var line in body.Split('\n'))
        {
            if (TryParseCriterionMappingLine(line, out var criterion, out _))
            {
                continue;
            }

            var prefix = CriterionMappingPrefix().Match(line);
            if (prefix.Success && int.TryParse(prefix.Groups["criterion"].Value, out criterion))
            {
                unparsedLines.TryAdd(criterion, line.Trim());
            }
        }

        for (var criterion = 1; criterion <= criterionCount; criterion++)
        {
            if (!mappingLines.ContainsKey(criterion))
            {
                diagnostic = unparsedLines.TryGetValue(criterion, out var offendingLine)
                    ? $"acceptance criterion mapping is incomplete: criterion {criterion} is unmapped; a line for criterion {criterion} was found but was not parsed as a mapping: '{BoundOffendingLine(offendingLine)}'; keep your existing plan and re-emit this criterion as a single line such as \"{criterion}. disposition=planned; plan=<mapping>\" or \"{criterion}. disposition=undecidable; would-settle=<evidence>; required-source=<producer/store>; unavailable-because=<reason>\""
                    : $"acceptance criterion mapping is incomplete: criterion {criterion} is unmapped; no line was found for criterion {criterion}";
                return false;
            }
        }

        // Historical durable receipts predate dispositions and remain readable. Once a plan uses the
        // new grammar, every criterion must use it so one undecidable item cannot hide an unmapped peer.
        var usesDispositionGrammar = mappingLines.Values.Any(mapping =>
            Regex.IsMatch(mapping, @"(?i)\bdisposition\s*="));
        if (!usesDispositionGrammar)
        {
            return true;
        }

        for (var criterion = 1; criterion <= criterionCount; criterion++)
        {
            var fields = ParseCriterionMappingFields(mappingLines[criterion]);
            if (!fields.TryGetValue("disposition", out var disposition))
            {
                diagnostic = $"acceptance criterion {criterion} must declare disposition=planned or disposition=undecidable";
                return false;
            }

            if (disposition.Equals("planned", StringComparison.OrdinalIgnoreCase))
            {
                if (!fields.TryGetValue("plan", out var plannedMapping) || string.IsNullOrWhiteSpace(plannedMapping))
                {
                    diagnostic = $"acceptance criterion {criterion} with disposition=planned must include a non-empty plan";
                    return false;
                }

                continue;
            }

            if (!disposition.Equals("undecidable", StringComparison.OrdinalIgnoreCase))
            {
                diagnostic = $"acceptance criterion {criterion} has unknown disposition '{disposition}'";
                return false;
            }

            foreach (var requiredField in new[] { "would-settle", "required-source", "unavailable-because" })
            {
                if (!fields.TryGetValue(requiredField, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    diagnostic = $"acceptance criterion {criterion} with disposition=undecidable must include a non-empty {requiredField}";
                    return false;
                }
            }
        }

        return true;
    }

    internal static IReadOnlyDictionary<int, string> SelectCriterionMappings(string mappingSectionBody)
    {
        var mappings = new Dictionary<int, string>();
        foreach (var line in mappingSectionBody.Split('\n'))
        {
            if (!TryParseCriterionMappingLine(line, out var criterion, out var mapping))
            {
                continue;
            }

            if (!mappings.TryGetValue(criterion, out var selected) ||
                (!Regex.IsMatch(selected, @"(?i)\bdisposition\s*=") &&
                 Regex.IsMatch(mapping, @"(?i)\bdisposition\s*=")))
            {
                mappings[criterion] = mapping;
            }
        }

        return new System.Collections.ObjectModel.ReadOnlyDictionary<int, string>(mappings);
    }

    internal static bool TryParseCriterionMappingLine(
        string line,
        out int criterion,
        out string mapping)
    {
        criterion = 0;
        mapping = string.Empty;
        var match = CriterionMappingLine().Match(line);
        if (!match.Success || !int.TryParse(match.Groups["criterion"].Value, out criterion))
        {
            return false;
        }

        mapping = match.Groups["mapping"].Value.Trim();
        var openingMarker = match.Groups["open"].Value;
        var skipped = match.Groups["skip"].Value;
        if (openingMarker.Length > 0 &&
            !skipped.Contains(openingMarker, StringComparison.Ordinal) &&
            mapping.EndsWith(openingMarker, StringComparison.Ordinal))
        {
            mapping = mapping[..^openingMarker.Length].TrimEnd();
        }

        return true;
    }

    private static string BoundOffendingLine(string line) =>
        line.Length <= MaxOffendingLineChars
            ? line
            : line[..(MaxOffendingLineChars - 1)] + "…";

    private static string DescribeOffendingPlanLine(string plan, int offset)
    {
        offset = Math.Clamp(offset, 0, plan.Length);
        var lineNumber = 1;
        var lineStart = 0;
        for (var index = 0; index < offset; index++)
        {
            if (plan[index] == '\n' ||
                (plan[index] == '\r' && (index + 1 >= plan.Length || plan[index + 1] != '\n')))
            {
                lineNumber++;
                lineStart = index + 1;
            }
        }

        var lineEnd = lineStart;
        while (lineEnd < plan.Length && plan[lineEnd] is not ('\r' or '\n'))
        {
            lineEnd++;
        }

        return $"Offending plan line {lineNumber}: '{BoundOffendingLine(plan[lineStart..lineEnd].Trim())}'";
    }

    private static Dictionary<string, string> ParseCriterionMappingFields(string mapping)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in CriterionMappingField().Matches(mapping))
        {
            fields[match.Groups["name"].Value] = match.Groups["value"].Value.Trim();
        }

        return fields;
    }

    // Locate the criterion number first, then scan only over complete emphasis runs or one-level
    // parenthesized/bracketed asides before requiring the existing mapping separator grammar.
    [GeneratedRegex(@"^[ \t]*(?:[-*][ \t]+)?(?<open>\*{1,2}|_{1,2})?(?:criterion[ \t]+)?(?<criterion>\d+)\b(?<skip>(?:[ \t]*(?:\*{1,2}|_{1,2}|\([^()\n]*\)|\[[^\[\]\n]*\]))*)(?:[ \t]*[.)\]:\-–—][ \t]*(?:(?:maps?(?:[ \t]+to)?|covers)[ \t]+)?|[ \t]+(?:maps?(?:[ \t]+to)?|covers|→|=>|[–—])[ \t]*)(?<mapping>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CriterionMappingLine();

    [GeneratedRegex(@"^[ \t]*(?:#{1,6}[ \t]+)?(?:[-*][ \t]+)?(?:\*{1,2}|_{1,2})?(?:criterion[ \t]+)?(?<criterion>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CriterionMappingPrefix();

    [GeneratedRegex(@"(?i)(?<name>disposition|plan|would-settle|required-source|unavailable-because)\s*=\s*(?<value>[^;]+)")]
    private static partial Regex CriterionMappingField();

    private static bool TryResolveCitedPaths(
        string plan,
        string workingDirectory,
        out string resolvedPlan,
        out string diagnostic)
    {
        resolvedPlan = plan;
        diagnostic = string.Empty;
        var substitutions = new List<CitationSubstitution>();
        var repositoryFilenameMatches = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? contextualDirectory = null;
        var contextualLineStart = -2;
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
            var lineStart = targetSection.LastIndexOf('\n', match.Index);
            if (lineStart != contextualLineStart)
            {
                contextualDirectory = null;
                contextualLineStart = lineStart;
            }

            var citation = match.Groups["citation"].Value.Trim();
            var citedPath = NormalizeCitedPath(citation);
            if (citedPath is null)
            {
                continue;
            }

            var hasExplicitDirectory = citedPath.Contains('/') || citedPath.Contains('\\');
            var isFullyQualifiedCitation = Path.IsPathFullyQualified(citedPath);
            var candidateCasingRoot = isFullyQualifiedCitation
                ? null
                : hasExplicitDirectory || contextualDirectory is null
                    ? workingDirectory
                    : contextualDirectory;
            var candidate = isFullyQualifiedCitation
                ? citedPath
                : hasExplicitDirectory || contextualDirectory is null
                    ? Path.Combine(workingDirectory, citedPath.Replace('/', Path.DirectorySeparatorChar))
                    : Path.Combine(contextualDirectory, citedPath);

            var inheritedNewFileMarker = false;
            if (!hasExplicitDirectory &&
                contextualDirectory is null &&
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
                    candidateCasingRoot = Path.GetDirectoryName(candidate);
                    inheritedNewFileMarker = distinctMatches[0].IsNewFile;
                }
            }

            if (hasExplicitDirectory)
            {
                var citesDirectory = Directory.Exists(candidate) ||
                    citedPath.EndsWith('/') ||
                    citedPath.EndsWith('\\');
                var candidateDirectory = citesDirectory ? candidate : Path.GetDirectoryName(candidate);
                contextualDirectory = candidateDirectory is not null && Directory.Exists(candidateDirectory)
                    ? candidateDirectory
                    : null;
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

            if (PathExistsWithExactCasing(candidate, candidateCasingRoot))
            {
                continue;
            }

            string? resolvedCandidate = null;
            string? resolvedCitation = null;
            foreach (var suffix in CandidatePathSuffixes)
            {
                var suffixedCandidate = candidate + suffix;
                if (!PathExistsWithExactCasing(suffixedCandidate, candidateCasingRoot))
                {
                    continue;
                }

                resolvedCandidate = suffixedCandidate;
                resolvedCitation = citation + suffix;
                break;
            }

            if (resolvedCandidate is not null && resolvedCitation is not null)
            {
                substitutions.Add(new CitationSubstitution(
                    targetHeading.Index + match.Groups["citation"].Index,
                    match.Groups["citation"].Length,
                    citation,
                    resolvedCitation));
                continue;
            }

            if (!hasExplicitDirectory && !isFullyQualifiedCitation)
            {
                var rootCandidate = Path.Combine(workingDirectory, citedPath);
                if (PathExistsWithExactCasing(rootCandidate, workingDirectory))
                {
                    continue;
                }

                foreach (var suffix in CandidatePathSuffixes)
                {
                    if (!PathExistsWithExactCasing(rootCandidate + suffix, workingDirectory))
                    {
                        continue;
                    }

                    resolvedCitation = citation + suffix;
                    break;
                }

                if (resolvedCitation is not null)
                {
                    substitutions.Add(new CitationSubstitution(
                        targetHeading.Index + match.Groups["citation"].Index,
                        match.Groups["citation"].Length,
                        citation,
                        resolvedCitation));
                    continue;
                }
            }

            if (!hasExplicitDirectory)
            {
                var filename = Path.GetFileName(citedPath);
                if (!string.IsNullOrWhiteSpace(filename) && CitedFilePath().IsMatch(citation))
                {
                    if (!repositoryFilenameMatches.TryGetValue(filename, out var repositoryMatches))
                    {
                        repositoryMatches = ResolveRepositoryFilesByName(workingDirectory, filename);
                        repositoryFilenameMatches[filename] = repositoryMatches;
                    }

                    if (repositoryMatches.Length == 1)
                    {
                        continue;
                    }

                    if (repositoryMatches.Length > 1)
                    {
                        var ambiguousCitationStart = targetHeading.Index + match.Groups["citation"].Index;
                        var candidates = string.Join(
                            ", ",
                            repositoryMatches.Select(path => $"'{Path.GetRelativePath(workingDirectory, path).Replace(Path.DirectorySeparatorChar, '/')}'"));
                        diagnostic =
                            $"target citation '{citation}' is ambiguous; at least these repository files match: {candidates}; source span [{ambiguousCitationStart}..{ambiguousCitationStart + citation.Length})." +
                            $"{Environment.NewLine}Offending citation: '{citation}'" +
                            $" {DescribeOffendingPlanLine(plan, ambiguousCitationStart)}";
                        return false;
                    }
                }
            }

            var citationStart = targetHeading.Index + match.Groups["citation"].Index;
            var suggestion = FindSingleCaseInsensitiveSuggestion(
                candidate,
                citation,
                hasExplicitDirectory,
                workingDirectory);
            if (!CitedFilePath().IsMatch(citation) && suggestion is null)
            {
                continue;
            }

            if (suggestion is null && IsRuntimeJsonArtifactMention(citedPath))
            {
                continue;
            }

            var suggestionText = suggestion is null
                ? string.Empty
                : $" Did you mean `{suggestion}`?";
            diagnostic =
                $"target citation '{citation}' does not exist and is not marked as a new file; source span [{citationStart}..{citationStart + citation.Length})." +
                $"{suggestionText}{Environment.NewLine}Offending citation: '{citation}'" +
                $" {DescribeOffendingPlanLine(plan, citationStart)}";
            return false;
        }

        foreach (var substitution in substitutions.OrderByDescending(item => item.Start))
        {
            resolvedPlan = resolvedPlan[..substitution.Start] +
                substitution.ResolvedCitation +
                resolvedPlan[(substitution.Start + substitution.Length)..];
        }

        if (substitutions.Count > 0)
        {
            var notes = substitutions.Select(substitution =>
                $"Planner contract note: raw citation `{substitution.RawCitation}` resolved to `{substitution.ResolvedCitation}`.");
            resolvedPlan = resolvedPlan.TrimEnd() + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, notes);
            if (resolvedPlan.Length > MaxPlanChars)
            {
                diagnostic = $"resolved plan is {resolvedPlan.Length} characters after citation audit notes; maximum durable size is {MaxPlanChars}";
                resolvedPlan = string.Empty;
                return false;
            }
        }

        return true;
    }

    private static string[] ResolveRepositoryFilesByName(string workingDirectory, string filename)
    {
        var matches = new List<string>(capacity: 2);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(workingDirectory);

        while (pendingDirectories.Count > 0 && matches.Count < 2)
        {
            var directory = pendingDirectories.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (string.Equals(Path.GetFileName(file), filename, StringComparison.Ordinal))
                    {
                        matches.Add(file);
                        if (matches.Count == 2)
                        {
                            break;
                        }
                    }
                }

                if (matches.Count == 2)
                {
                    break;
                }

                foreach (var childDirectory in Directory.EnumerateDirectories(directory).OrderByDescending(path => path, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(childDirectory);
                    if (IsExcludedRepositorySearchDirectory(name) ||
                        (File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pendingDirectories.Push(childDirectory);
                }
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An unreadable subtree cannot contribute a safe unique match.
            }
        }

        return matches.ToArray();
    }

    private static bool IsExcludedRepositorySearchDirectory(string name) =>
        name.StartsWith(".", StringComparison.Ordinal) ||
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TestResults", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("playwright-report", StringComparison.OrdinalIgnoreCase);

    private static bool PathExistsWithExactCasing(string path, string? citationCasingRoot)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var traversalRoot = citationCasingRoot is null
                ? Path.GetPathRoot(fullPath)
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(citationCasingRoot));
            if (string.IsNullOrEmpty(traversalRoot))
            {
                return false;
            }

            var current = traversalRoot;
            var relative = citationCasingRoot is null
                ? fullPath[traversalRoot.Length..]
                : Path.GetRelativePath(traversalRoot, fullPath);
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    var parent = Directory.GetParent(current);
                    if (parent is null)
                    {
                        return false;
                    }

                    current = parent.FullName;
                    continue;
                }

                var exactEntry = Directory
                    .EnumerateFileSystemEntries(current)
                    .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.Ordinal));
                if (exactEntry is null)
                {
                    return false;
                }

                current = exactEntry;
            }

            return true;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? FindSingleCaseInsensitiveSuggestion(
        string candidate,
        string citation,
        bool hasExplicitDirectory,
        string workingDirectory)
    {
        var matches = new List<string>();
        foreach (var probe in new[] { candidate }.Concat(CandidatePathSuffixes.Select(suffix => candidate + suffix)))
        {
            var parent = Path.GetDirectoryName(probe);
            var name = Path.GetFileName(probe);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name) || !Directory.Exists(parent))
            {
                continue;
            }

            try
            {
                matches.AddRange(Directory
                    .EnumerateFileSystemEntries(parent)
                    .Where(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // An unreadable directory cannot provide a reliable casing classification.
            }
        }

        var distinctMatches = matches.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctMatches.Length != 1)
        {
            return null;
        }

        if (Path.IsPathFullyQualified(citation))
        {
            return distinctMatches[0];
        }

        return hasExplicitDirectory
            ? Path.GetRelativePath(workingDirectory, distinctMatches[0]).Replace(Path.DirectorySeparatorChar, '/')
            : Path.GetFileName(distinctMatches[0]);
    }

    private sealed record CitationSubstitution(
        int Start,
        int Length,
        string RawCitation,
        string ResolvedCitation);

    private static string? NormalizeCitedPath(string citation)
    {
        if (citation.Contains('(') || citation.Contains(')'))
        {
            return null;
        }

        var fileMatch = CitedFilePath().Match(citation);
        if (fileMatch.Success)
        {
            var path = fileMatch.Groups["path"].Value;
            return ContainsProsePathCharacters(path) ? null : path;
        }

        if (SpacedPathSeparator().IsMatch(citation))
        {
            return null;
        }

        return (citation.Contains('/') || citation.Contains('\\')) && !ContainsProsePathCharacters(citation)
            ? citation
            : null;
    }

    private static bool ContainsProsePathCharacters(string path)
    {
        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (char.IsWhiteSpace(character) || character is '<' or '>' ||
                (character == '$' && index + 1 < path.Length && char.IsAsciiLetter(path[index + 1])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRuntimeJsonArtifactMention(string path)
    {
        if (!path.EndsWith(".json", StringComparison.Ordinal) || path.IndexOfAny(['*', '?']) >= 0)
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        if (!normalized.Contains('/'))
        {
            return true;
        }

        var firstSegmentStart = normalized.StartsWith('/') ? 1 : 0;
        var separator = normalized.IndexOf('/', firstSegmentStart);
        return separator >= 0 && normalized[firstSegmentStart..separator]
            .Equals(".orchestrator", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRequiredSectionEvidence(string label, string body)
    {
        return label switch
        {
            "premise validity" =>
                HasMinimumLexicalDiversity(body),
            "target seams and symbols" =>
                body.Contains('`') &&
                (TargetCitation().IsMatch(body) || HasAdmittedRepositoryPathCitation(body)),
            "ownership and lifecycle" =>
                HasMinimumLexicalDiversity(body),
            "external and edge contracts" =>
                HasMinimumLexicalDiversity(body),
            "integration seams" =>
                !IntegrationPlaceholderMarker().IsMatch(body) &&
                (HasMinimumLexicalDiversity(body) ||
                 HasSubstantivelyOrderedNumberedList(body)),
            "verification commands and classes" =>
                body.Contains('`') &&
                (body.Contains("TEST-VERIFIABLE", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("REAL-WORLD-DEPENDENT", StringComparison.OrdinalIgnoreCase)),
            "risks and stop conditions" =>
                HasMinimumLexicalDiversity(body),
            _ => false
        };
    }

    private static bool HasAdmittedRepositoryPathCitation(string body)
    {
        string[] prefixes = ["docs/", "scripts/", "config/"];
        foreach (Match match in BacktickedCitation().Matches(body))
        {
            var path = NormalizeCitedPath(match.Groups["citation"].Value.Trim());
            if (path is null)
            {
                continue;
            }

            path = path.Replace('\\', '/');
            foreach (var prefix in prefixes)
            {
                if (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasMinimumLexicalDiversity(string body)
    {
        var distinctWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SubstantiveWord().Matches(body))
        {
            distinctWords.Add(match.Value);
            if (distinctWords.Count >= MinimumDistinctSectionWords)
            {
                return true;
            }
        }

        return false;
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
                $"must contain at least {MinimumDistinctSectionWords} distinct words in its section body",
            "target seams and symbols" =>
                "must cite a concrete target seam or symbol in backticks in its section body",
            "ownership and lifecycle" =>
                $"must contain at least {MinimumDistinctSectionWords} distinct words in its section body",
            "external and edge contracts" =>
                $"must contain at least {MinimumDistinctSectionWords} distinct words in its section body",
            "integration seams" =>
                $"must contain no TBD, TODO, or placeholder markers and either at least {MinimumDistinctSectionWords} distinct words of prose, or a sequential numbered list with at least two non-placeholder items, in its section body",
            "verification commands and classes" =>
                "must include a backticked verification command or class and identify its verification class in its section body",
            "risks and stop conditions" =>
                $"must contain at least {MinimumDistinctSectionWords} distinct words in its section body",
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

    [GeneratedRegex(@"(?m)^[ \t]*(?<number>\d+)[.)][ \t]+(?<content>\S[^\r\n]*)$")]
    private static partial Regex NumberedIntegrationItem();

    [GeneratedRegex(@"(?im)\b(?:tbd|todo)\b|^[ \t]*(?:(?:\d+[.)]|[-*•])[ \t]+)?placeholder[ \t.:;,!?]*$")]
    private static partial Regex IntegrationPlaceholderMarker();

    [GeneratedRegex(@"\p{L}[\p{L}\p{Nd}]*")]
    private static partial Regex SubstantiveWord();

    [GeneratedRegex(@"(?i)(?:\bnew[ \t]+file\b|\b(?:create|add)\b(?:[ \t]+(?:a|an|the|new))?)[^`\r\n]{0,24}$")]
    private static partial Regex NewFileCitationPrefix();

    [GeneratedRegex(@"(?i)^[ \t]*(?:—[ \t]*new[ \t]+(?:file\b|(?:[A-Za-z][A-Za-z/-]*[ \t]+){0,3}(?:store|class|record|interface|test|fixture|script|document|receipt|file)\b(?=[ \t]*(?:[.,;:]|$)))|\([ \t]*new[ \t]+file[ \t]*\)|\([ \t]*new[ \t]+file[ \t]*[,;:][ \t]*[^)\s][^)]*\))")]
    private static partial Regex NewFileCitationSuffix();

    [GeneratedRegex(@"\s[/\\]\s")]
    private static partial Regex SpacedPathSeparator();

    [GeneratedRegex(@"`(?<citation>[^`\r\n]+)`")]
    private static partial Regex BacktickedCitation();

    [GeneratedRegex(@"(?i)^(?<path>.+?\.(?-i:cs|csproj|ps1|md|json|yml|yaml|props|targets|txt))(?:(?::\d+(?:-\d+)?)|(?:#L\d+(?:-L?\d+)?)|(?:::.+))?$")]
    private static partial Regex CitedFilePath();

    [GeneratedRegex(@"(?m)^[ \t]{0,3}#{1,6}[ \t]+")]
    private static partial Regex MarkdownHeading();
}
