using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorStewardDeterministicRoute(
    IConductorStewardTrackedFileLister? files = null,
    IConductorStewardLaneSubstringResolver? lanes = null)
{
    private const string RejectionMarker = "[orchestrator Planner output contract rejection]";
    private const string SameLineRule = "Write the disposition and non-empty one-sentence plan summary on the same mapping line: N. maps to <subject>. disposition=planned; plan=<non-empty one-sentence summary>.";
    private const string ConcreteFileRule = "Cite a concrete repository-relative file path that exists at HEAD, without wildcards, or explicitly mark the path as new.";
    private static readonly Regex Unmapped = new(@"\bcriterion (?<number>\d+) is unmapped\b", RegexOptions.CultureInvariant);
    private static readonly Regex MissingPlan = new(@"acceptance criterion (?<number>\d+) with disposition=planned must include a non-empty plan", RegexOptions.CultureInvariant);
    private static readonly Regex Citation = new(@"target citation '(?<path>[^']+)' does not exist and is not marked as a new file", RegexOptions.CultureInvariant);
    private static readonly Regex AmbiguousCitation = new(@"target citation '(?<path>[^']+)' is ambiguous; at least these repository files match:[ \t]*(?<candidates>[^;\r\n]*)", RegexOptions.CultureInvariant);
    private static readonly Regex QuotedCandidate = new(@"'(?<path>[^']+)'", RegexOptions.CultureInvariant);
    private static readonly Regex Section = new(@"missing required section '(?<section>[^']+)'", RegexOptions.CultureInvariant);
    private static readonly Regex Class = new("Disabled-collection test class ['\"](?<name>[^'\"]+)['\"]", RegexOptions.CultureInvariant);

    internal string? TryBuild(ConductorStewardTrigger trigger, string worktree)
    {
        if (trigger.Kind is ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed or
            ConductorStewardTriggerKind.DeveloperGateReopenNoCommit or
            ConductorStewardTriggerKind.DeveloperReviewerFindingNoCommit)
            return null;
        var text = trigger.Kind switch
        {
            ConductorStewardTriggerKind.ReviewerOrTesterBlockerWithAnswer => BuildAnsweredBlockerText(trigger),
            ConductorStewardTriggerKind.AcceptanceCollectionGuardClass => BuildCollectionText(trigger, worktree),
            _ => BuildPlannerText(trigger, worktree)
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonSerializer.Serialize(new
        {
            kind = "route",
            targetTaskId = trigger.TaskId,
            cause = "ContractClarification",
            reversibility = "reversible",
            text,
            instruction = text,
            evidenceReferences = Array.Empty<string>()
        });
    }

    private static string? BuildAnsweredBlockerText(ConductorStewardTrigger trigger)
    {
        var blocker = trigger.EvidenceReferences.FirstOrDefault(reference => reference.StartsWith("blocker=", StringComparison.Ordinal))?["blocker=".Length..];
        var answer = trigger.EvidenceReferences.FirstOrDefault(reference => reference.StartsWith("clarification-answer=", StringComparison.Ordinal))?["clarification-answer=".Length..];
        if (string.IsNullOrWhiteSpace(blocker) || string.IsNullOrWhiteSpace(answer)) return null;
        return $"Resolve the Reviewer/Tester blocker on candidate {trigger.CandidateSha}: {blocker}\n" +
               $"Apply this authoritative clarification answer:\n{answer}\nRecheck the affected acceptance criteria.";
    }

    private string? BuildPlannerText(ConductorStewardTrigger trigger, string worktree)
    {
        var evidence = trigger.Evidence;
        var marker = evidence.LastIndexOf(RejectionMarker, StringComparison.Ordinal);
        if (marker >= 0) evidence = evidence[(marker + RejectionMarker.Length)..];
        if (string.IsNullOrWhiteSpace(evidence)) return null;
        var corrections = new List<string>();
        var section = Section.Match(evidence);
        if (section.Success)
            corrections.Add($"Missing required section '{section.Groups["section"].Value}'. Include that exact heading and its substantive content. Required headings are premise validity; acceptance criterion mapping; target seams and symbols; ownership and lifecycle; integration seams; verification commands and classes; risks and stop conditions; external and edge contracts. Offending diagnostic: '{section.Value}'.");
        var unmapped = Unmapped.Match(evidence);
        if (unmapped.Success)
        {
            var number = unmapped.Groups["number"].Value;
            var unparsed = Regex.Match(evidence, @"was not parsed as a mapping: '(?<line>[^']*)'", RegexOptions.CultureInvariant);
            corrections.Add($"Acceptance criterion {number} is unmapped. {SameLineRule}" +
                (unparsed.Success ? $" Offending line: '{unparsed.Groups["line"].Value}'." : string.Empty));
        }
        var missingPlan = MissingPlan.Match(evidence);
        if (missingPlan.Success)
        {
            var number = missingPlan.Groups["number"].Value;
            corrections.Add($"Acceptance criterion {number} with disposition=planned must include a non-empty plan. {SameLineRule}");
        }
        var citation = Citation.Match(evidence);
        if (citation.Success)
        {
            if (files is null) return null;
            var path = citation.Groups["path"].Value;
            var stem = CitationStem(path);
            if (stem.Length == 0) return null;
            IReadOnlyList<string> matches;
            try { matches = files.MatchingFiles(worktree, stem); }
            catch (Exception) { return null; }
            corrections.Add($"Offending target citation: '{path}'. {ConcreteFileRule} {SameLineRule} " +
                (matches.Count == 0
                    ? $"No tracked file matches the cited stem '{stem}'. That means the cited text is not a repository file: remove the backticks and describe it in prose, or mark it as a new file if the plan creates it."
                    : $"Tracked files matching the cited stem '{stem}': {string.Join(", ", matches)}."));
        }
        foreach (Match ambiguous in AmbiguousCitation.Matches(evidence))
        {
            var candidates = QuotedCandidate.Matches(ambiguous.Groups["candidates"].Value)
                .Select(match => match.Groups["path"].Value)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            if (candidates.Length == 0) continue;
            corrections.Add($"Offending ambiguous target citation: '{ambiguous.Groups["path"].Value}'. " +
                $"Cite exactly one of these full repository-relative paths on the same mapping line: {string.Join(", ", candidates.Select(path => $"'{path}'"))}. " +
                $"{ConcreteFileRule} {SameLineRule}");
        }
        return corrections.Count == 0 ? null : string.Join(" ", corrections);
    }

    private string? BuildCollectionText(ConductorStewardTrigger trigger, string worktree)
    {
        var className = trigger.EvidenceReferences.FirstOrDefault(reference => reference.StartsWith("offending-class=", StringComparison.Ordinal))?["offending-class=".Length..]
            ?? Class.Match(trigger.Evidence).Groups["name"].Value;
        var collection = trigger.EvidenceReferences.FirstOrDefault(reference => reference.StartsWith("collection=", StringComparison.Ordinal))?["collection=".Length..];
        if (string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(collection) || lanes is null) return null;
        string? substring;
        try { substring = lanes.RequiredSubstring(worktree, collection); }
        catch (Exception) { return null; }
        return string.IsNullOrWhiteSpace(substring) ? null :
            $"Disabled-collection test class '{className}' belongs to collection '{collection}'. Rename the class so its name contains the acceptance-lane substring '{substring}', then check the collection guard.";
    }

    private static string CitationStem(string citation)
    {
        var path = Regex.Split(citation, @"::|#L\d+|:\d+")[0].Replace('\\', '/');
        return Path.GetFileNameWithoutExtension(path).Trim('*', '?');
    }
}
