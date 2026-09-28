using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed record HumanInputDirective(
    string Question,
    string QuestionFingerprint,
    string? BlockerFingerprint = null,
    HumanWaitKind Kind = HumanWaitKind.SpecClarification,
    string? EvidenceOwner = null);

public sealed record HumanInputDirectiveParseResult(HumanInputDirective? Directive, string? Diagnostic)
{
    public bool IsMalformed => !string.IsNullOrWhiteSpace(Diagnostic);
}

public static class AgentOutputDirectives
{
    private static readonly string[] NoHumanInputMarkers = ["none", "no", "not needed", "no input needed"];
    private static readonly string[] BaseWorkerResultFields =
        ["files", "commands", "tests", "blockers", "model_fit", "skills", "confidence"];

    public static IReadOnlyList<string> WorkerResultTemplateLines =>
        WorkerResultTemplateLinesForRole(null);

    public static IReadOnlyList<string> WorkerResultTemplateLinesForRole(AgentRole? role)
    {
        var lines = new List<string>();
        if (role == AgentRole.Developer)
        {
            lines.Add(
                "Developer: set `assigned_scope_complete` to `true` only when your assigned implementation is complete; `false` requests bounded revision and excludes later Acceptance/operator evidence.");
        }
        else if (role == AgentRole.Planner)
        {
            lines.Add(
                "Planner: print the complete decision-changing plan in stdout before WORKER_RESULT; stdout is authoritative, and a summary or private model-home file path alone is invalid. " +
                "A referenced plan artifact is only a fallback when stdout parsing fails; writing a separate artifact is not required. " +
                "Use these exact substantive headings: ## Premise validity; ## Acceptance criteria mapping; ## Target seams and symbols; " +
                "## Ownership and lifecycle; ## External and edge contracts; ## Integration seams; ## Verification commands and classes; ## Risks and stop conditions. " +
                "Map every numbered acceptance criterion, and state valid/invalid premise evidence, backticked file/symbol citations, the owner/lifecycle decision, external and unhappy-path contracts, integration sequence, " +
                "For every criterion mapping use `disposition=planned; plan=<mapping>` or `disposition=undecidable; would-settle=<evidence>; required-source=<producer/store>; unavailable-because=<reason>`. " +
                "Begin each criterion mapping line with the bare criterion number followed by a period, with no heading, bullet or bold prefix. " +
                "Keep the text after `plan=` a non-empty one-sentence summary on that same line; any detail bullets that follow must not begin with a digit and a period. " +
                "An undecidable criterion does not block other criteria and requires `blockers: none` when no operator action is needed. " +
                "For evidence that exists only in an unreadable store, emit exactly one `PLANNER_EVIDENCE_REQUEST:` JSON directive with criterion_index, evidence_key, availability=retrievable, store, needed, and reason. " +
                "For evidence that was never recorded, prefer an undecidable mapping; if operator action is still required, use availability=never-recorded and omit store. " +
                "Evidence needed to decide or plan now is a blocking prerequisite; evidence that can only be produced after the candidate exists is prospective acceptance evidence. " +
                "For the latter, use availability=post-implementation with owner and omit store; retain the exact criterion obligation without claiming the check was executed. " +
                "backticked verification commands with TEST-VERIFIABLE or REAL-WORLD-DEPENDENT, and explicit stop conditions. " +
                "Cited repository paths must exist unless explicitly marked as a new file to create. " +
                "Cite every path as a concrete repository-relative file, never a wildcard pattern, and before finishing confirm each cited path exists, for example with `git ls-files <path>`, unless it is marked as a new file. " +
                "Mark a new file with the exact token `(new file)` or `— new file` immediately after its backticked path, " +
                "or use `— new` followed by up to three descriptive words and an artifact-kind noun (`store`, `class`, `record`, `interface`, `test`, `fixture`, `script`, `document`, or `receipt`) that ends the clause. " +
                "The descriptive form must end at a period, semicolon, or the end of the line. Alternatively, use the words 'new file', 'create', or 'add' within 24 characters before the path containing no other backtick. " +
                "A marker applies only to the single citation it is adjacent to, not to the whole line, so mark every new path individually. " +
                "To cite a symbol, use a double colon such as `Path/File.cs::SymbolName`, or `Path/File.cs:123`, or `Path/File.cs#L12`; " +
                "a single colon before a symbol name is not recognised and the whole string is then treated as a file path that does not exist.");
        }
        else if (role == AgentRole.Researcher)
        {
            lines.Add(
                "Researcher: print the complete evidence-backed research artifact in stdout before WORKER_RESULT. " +
                "Use these exact substantive headings: ## Current source findings; ## Prior goal evidence; " +
                "## Upstream capabilities; ## Likely seams and risks. A summary or provider-private path alone is invalid.");
        }

        lines.Add("WORKER_RESULT:");
        lines.AddRange(WorkerResultFieldTemplateLinesForRole(role));
        lines.Add("END_WORKER_RESULT");
        return lines;
    }

    public static IReadOnlyList<string> WorkerResultFieldNames => BaseWorkerResultFields;

    public static IReadOnlyList<string> RequiredWorkerResultFieldNamesForRole(AgentRole role) =>
        role switch
        {
            AgentRole.Researcher => [.. BaseWorkerResultFields, "citations"],
            AgentRole.Reviewer => [.. BaseWorkerResultFields, "findings", "touched_anchors", "criteria_verdicts", "verdict"],
            _ => BaseWorkerResultFields
        };

    private static IReadOnlyList<string> WorkerResultFieldTemplateLinesForRole(AgentRole? role)
    {
        var lines = new List<string>
        {
            "files: <comma-separated changed files or none>",
            "commands: <commands run or none>",
            "tests: <pass|fail|not-run|deferred|inconclusive - token first, then current-round evidence>",
            "commit: <commit sha or none>",
            role == AgentRole.Reviewer
                ? "blockers: <none - token first when verdict is pass; otherwise one complete semicolon-delimited token per open blocking finding, each with file:line, severity blocking, and a violated acceptance criterion; no literal semicolons inside a token; blank is invalid; advisory findings belong only in findings>"
                : "blockers: <none|premise-invalid - fact and evidence (Planner/Researcher only)|exact-blocker - token first; blank is invalid; put deferred-verification notes in tests>"
        };

        if (role == AgentRole.Researcher)
        {
            lines.Add("citations: <repository files, docs, or evidence sources used>");
        }
        else if (role == AgentRole.Developer)
        {
            lines.Add("assigned_scope_complete: <true|false>");
        }
        else if (role is AgentRole.Reviewer or AgentRole.Tester)
        {
            lines.Add("findings: <one-line JSON array of {stable_id,state:open|resolved,severity:blocking|advisory,category:spec-compliance|spec-defect|correctness|test-evidence|test-coverage|code-quality|operator-owned|acceptance-owned,location:{file,region,hunk?},description,evidence_request?:{selections:[{test_project,test_class}]}}; severity is required; open blocking test-evidence requires evidence_request for Tester and Reviewer; [] when none>");
            lines.Add("touched_anchors: <one-line JSON array of {file,region,hunk?} for prior finding anchors touched by this round's diff; [] when none>");
            if (role == AgentRole.Reviewer)
            {
                lines.Add("criteria_verdicts: <one-line JSON array of {criterion_index,verdict:met|not-met|not-verifiable,evidence}; [] when the goal has no refined acceptance criteria>");
                lines.Add("verdict: <pass|needs-work|fail>");
            }
        }

        lines.AddRange(
        [
            "model_fit: <provider/model - adequate|overkill|underpowered - task shape - reason>",
            "skills: <selected skills used or none>",
            "confidence: <high|medium|low>"
        ]);

        return lines;
    }

    public static string? TryParseHumanInputRequest(string output)
        => ParseHumanInputRequest(output).Directive?.Question;

    public static HumanInputDirectiveParseResult ParseHumanInputRequest(string output, AgentRole? role = null)
    {
        var lines = output.Split(["\r\n", "\n"], StringSplitOptions.None);
        var evidenceRequests = lines
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("PLANNER_EVIDENCE_REQUEST:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (evidenceRequests.Length > 1)
        {
            return MalformedEvidenceRequest("exactly one evidence request may be emitted per round");
        }

        if (evidenceRequests.Length == 1)
        {
            if (role is not null && role != AgentRole.Planner)
            {
                return new HumanInputDirectiveParseResult(
                    null,
                    "PLANNER_EVIDENCE_REQUEST is valid only for the Planner role");
            }

            var evidenceRequest = evidenceRequests[0];
            return ParsePlannerEvidenceRequest(evidenceRequest["PLANNER_EVIDENCE_REQUEST:".Length..].Trim());
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var question = TryReadDirective(trimmed, "HUMAN_INPUT:")
                ?? TryReadDirective(trimmed, "HUMAN INPUT:");
            if (!string.IsNullOrWhiteSpace(question))
            {
                return new HumanInputDirectiveParseResult(
                    new HumanInputDirective(question, HumanInputRequest.BuildQuestionFingerprint(question)),
                    null);
            }
        }

        return new HumanInputDirectiveParseResult(null, null);
    }

    private static HumanInputDirectiveParseResult ParsePlannerEvidenceRequest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return MalformedEvidenceRequest("payload must be a JSON object");
            }

            var root = document.RootElement;
            if (!TryGetPositiveInt(root, "criterion_index", out var criterionIndex) ||
                !TryGetRequiredString(root, "evidence_key", out var evidenceKey) ||
                !TryGetRequiredString(root, "availability", out var availability) ||
                !TryGetRequiredString(root, "needed", out var needed) ||
                !TryGetRequiredString(root, "reason", out var reason))
            {
                return MalformedEvidenceRequest(
                    "criterion_index must be positive and evidence_key, availability, needed, and reason must be non-empty");
            }

            var hasStore = TryGetRequiredString(root, "store", out var store);
            string availabilityText;
            var kind = HumanWaitKind.PlannerPrerequisiteEvidence;
            string? evidenceOwner = null;
            if (availability.Equals("retrievable", StringComparison.OrdinalIgnoreCase))
            {
                if (!hasStore)
                {
                    return MalformedEvidenceRequest("retrievable evidence must name a non-empty store");
                }

                availabilityText = $"retrievable from store '{store}', which the worker cannot reach";
            }
            else if (availability.Equals("never-recorded", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("store", out _))
                {
                    return MalformedEvidenceRequest("never-recorded evidence must not name a store");
                }

                availabilityText = "never-recorded; the evidence does not exist in an available record";
            }
            else if (availability.Equals("post-implementation", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetRequiredString(root, "owner", out var owner))
                {
                    return MalformedEvidenceRequest("post-implementation evidence must name a non-empty owner");
                }

                if (root.TryGetProperty("store", out _))
                {
                    return MalformedEvidenceRequest("post-implementation evidence must not name a store");
                }

                availabilityText = $"post-implementation; produced after the candidate exists. Owner: {owner}";
                kind = HumanWaitKind.ProspectiveAcceptanceEvidence;
                evidenceOwner = owner;
            }
            else
            {
                return MalformedEvidenceRequest(
                    "availability must be retrievable, never-recorded, or post-implementation");
            }

            var question =
                $"Planner evidence request for criterion {criterionIndex}: {needed}. " +
                $"Availability: {availabilityText}. Reason: {reason}";
            var fingerprint = HumanInputRequest.BuildPlannerEvidenceFingerprint(criterionIndex, evidenceKey);
            return new HumanInputDirectiveParseResult(
                new HumanInputDirective(question, fingerprint, fingerprint, kind, evidenceOwner),
                null);
        }
        catch (JsonException error)
        {
            return MalformedEvidenceRequest($"payload is not valid JSON: {error.Message}");
        }
    }

    private static HumanInputDirectiveParseResult MalformedEvidenceRequest(string diagnostic) =>
        new(null, $"Malformed PLANNER_EVIDENCE_REQUEST: {diagnostic}.");

    private static bool TryGetPositiveInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value) &&
               value > 0;
    }

    private static bool TryGetRequiredString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static string? TryReadDirective(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var directiveValue = value[prefix.Length..].Trim();
        return IsNoHumanInputMarker(directiveValue) ? null : directiveValue;
    }

    private static bool IsNoHumanInputMarker(string value)
    {
        var normalized = value.Trim().TrimEnd('.', '!', ';', ':').Trim();
        return NoHumanInputMarkers.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }
}
