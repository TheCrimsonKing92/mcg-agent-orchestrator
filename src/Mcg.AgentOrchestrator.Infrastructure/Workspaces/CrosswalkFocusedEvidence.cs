using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Plans local crosswalk evidence and grades each selector from normalized CTRF records.</summary>
internal static class CrosswalkFocusedEvidence
{
    private const string FocusedPrefix = "crosswalk focused evidence: ";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal enum SelectionGrade { Satisfied, Failed, NotDemonstrated }

    internal static AcceptanceManifestCheck? ReadDeclaredCheck(JsonElement manifestRoot)
    {
        if (!manifestRoot.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var element in checks.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;
            try
            {
                var check = element.Deserialize<AcceptanceManifestCheck>(JsonOptions);
                if (check?.Type?.Equals("crosswalk-test", StringComparison.OrdinalIgnoreCase) == true)
                    return check;
            }
            catch (JsonException)
            {
                // Unreadable entries contribute no declaration, as with Go and .NET checks.
            }
        }
        return null;
    }

    internal static bool Applies(AcceptanceGateEngineSettings? settings) =>
        settings?.DeclaredCrosswalkTestCheck is not null && settings.DeclaredGoTestCheck is null &&
        settings.MtpInvocations.Count == 0 && settings.DeclaredDotnetTestChecks.Count == 0;

    internal static bool TryResolveProject(string alias, AcceptanceGateEngineSettings? settings, out string project)
    {
        project = string.Empty;
        if (!Applies(settings) || string.IsNullOrWhiteSpace(alias))
            return false;
        project = settings!.DeclaredCrosswalkTestCheck!.Name;
        return true;
    }

    internal static bool TryBuild(
        string request, AcceptanceGateEngineSettings settings, string worktreePath,
        out IReadOnlyList<AcceptanceManifestCheck> checks, out FocusedEvidenceCoverage coverage,
        out FocusedEvidenceRejection rejection)
    {
        checks = [];
        coverage = new FocusedEvidenceCoverage(TargetToChecks: []);
        rejection = new(FocusedEvidenceRejectionCode.EmptyRequest, request, "empty evidence request");
        if (!Applies(settings))
            return false;
        var items = request.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Length == 0)
            return false;

        var declared = settings.DeclaredCrosswalkTestCheck!;
        var prefix = GoalAcceptanceVerifier.ProjectLabel(declared.Name) + ":";
        var selectors = new List<string>();
        foreach (var item in items)
        {
            // A declared name may contain a colon; remove its complete canonical label first.
            var separator = item.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Length - 1 : item.IndexOf(':');
            if (separator <= 0 || !IsSupportedSelector(item[(separator + 1)..]))
            {
                rejection = new(FocusedEvidenceRejectionCode.UnsupportedToken, item,
                    "Crosswalk evidence requires a project alias and an exact test id or project-relative test file.");
                return false;
            }
            selectors.Add(NormalizeSelector(item[(separator + 1)..]));
        }
        var check = new AcceptanceManifestCheck
        {
            Name = FocusedPrefix + declared.Name,
            Type = declared.Type,
            Project = declared.Project,
            Arguments = selectors.Distinct(StringComparer.Ordinal).ToArray(),
            TimeoutMinutes = declared.TimeoutMinutes,
            Advisory = declared.Advisory,
            IsFocusedEvidenceSelection = true
        };
        checks = [check];
        coverage = new FocusedEvidenceCoverage(
            items.Select(item => new FocusedEvidenceTargetCoverage(item, [check.Name])).ToArray(),
            ExecutionMode: "focused", ExecutionReason: "crosswalk-focused");
        return true;
    }

    internal static bool IsEvidenceCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("crosswalk-test", StringComparison.OrdinalIgnoreCase) &&
        check.Name.StartsWith(FocusedPrefix, StringComparison.Ordinal);

    internal static string[] SplitTarget(AcceptanceManifestCheck check, string target)
    {
        var label = GoalAcceptanceVerifier.ProjectLabel(check.Name[FocusedPrefix.Length..]);
        var prefix = label + ":";
        return target.StartsWith(prefix, StringComparison.Ordinal)
            ? [label, target[prefix.Length..]]
            : target.Split(':', 2, StringSplitOptions.TrimEntries);
    }

    internal static bool IsSupportedSelector(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector.IndexOfAny([';', '\r', '\n']) >= 0)
            return false;
        var normalized = NormalizeSelector(selector);
        var separator = normalized.IndexOf("::", StringComparison.Ordinal);
        var file = separator < 0 ? normalized : normalized[..separator];
        return file.Length > 0 && !file.StartsWith('/') && !file.Contains(':') &&
            !file.Contains('*') && !file.Contains('?') &&
            file.Split('/').All(part => part.Length > 0 && part is not ("." or "..")) &&
            (separator < 0 || !string.IsNullOrWhiteSpace(normalized[(separator + 2)..]));
    }

    internal static string[] ExpandSelectors(IReadOnlyList<string> selectors, IReadOnlyList<string> discoveredIds)
    {
        var normalized = selectors.Select(NormalizeSelector).ToHashSet(StringComparer.Ordinal);
        // Preserve discovery order and execute the union once. Invocation and batching belong to the gate.
        return discoveredIds.Where(id => normalized.Contains(NormalizeSelector(id)) ||
                normalized.Contains(FileSelector(id)))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static SelectionGrade Grade(IEnumerable<TestResult> records, string selection)
    {
        var selector = NormalizeSelector(selection);
        var passed = false;
        foreach (var record in records)
        {
            if (!string.Equals(selector, NormalizeSelector(record.Id), StringComparison.Ordinal) &&
                !string.Equals(selector, FileSelector(record.Id), StringComparison.Ordinal))
                continue;
            if (record.Status is TestResultStatus.Failed or TestResultStatus.Error)
                return SelectionGrade.Failed;
            passed |= record.Status == TestResultStatus.Passed;
        }
        return passed ? SelectionGrade.Satisfied : SelectionGrade.NotDemonstrated;
    }

    internal static IReadOnlyList<FindingEvidenceSelection> UndemonstratedSelections(
        AcceptanceManifestCheck check, IReadOnlyList<AcceptanceCheckResult> recorded,
        IReadOnlyList<FindingEvidenceSelection> selections)
    {
        var source = new NormalizedTestResultSource();
        var records = recorded.SelectMany(result => result.NormalizedTestResultPaths ?? [])
            .Distinct(StringComparer.Ordinal).Select(source.Read)
            .Where(result => result.Status == TestResultReadStatus.Readable)
            .SelectMany(result => result.Records).ToArray();
        return selections.Where(selection => Grade(records, selection.TestClass) == SelectionGrade.NotDemonstrated)
            .Distinct().ToArray();
    }

    private static string FileSelector(string id)
    {
        var normalized = NormalizeSelector(id);
        var separator = normalized.IndexOf("::", StringComparison.Ordinal);
        return separator < 0 ? normalized : normalized[..separator];
    }

    private static string NormalizeSelector(string selector)
    {
        var trimmed = selector.Trim();
        var separator = trimmed.IndexOf("::", StringComparison.Ordinal);
        var file = (separator < 0 ? trimmed : trimmed[..separator]).Replace('\\', '/');
        while (file.StartsWith("./", StringComparison.Ordinal))
            file = file[2..];
        return file + (separator < 0 ? "" : trimmed[separator..]);
    }
}
