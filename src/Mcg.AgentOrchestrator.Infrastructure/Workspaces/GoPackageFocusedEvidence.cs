using System.Text.Json;
using System.Text.RegularExpressions;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Maps Go-only evidence selections to command checks and interprets their verbose output.</summary>
internal static class GoPackageFocusedEvidence
{
    private const string FocusedPrefix = "go focused evidence: ";
    private const string FallbackPrefix = "go declared-check evidence: ";
    private const string PassMarker = "--- PASS: ";
    private const string FailMarker = "--- FAIL: ";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex PackagePattern = new(
        @"\A[A-Za-z0-9_][A-Za-z0-9_.-]*(/[A-Za-z0-9_][A-Za-z0-9_.-]*)*\z");
    private static readonly Regex TestNamePattern = new(@"\A(Test|Example|Fuzz)[A-Za-z0-9_]*\z");

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
                if (check is not null && IsGoTestCommand(check))
                    return check;
            }
            catch (JsonException)
            {
                // Unreadable entries contribute no declaration, just as for dotnet-test checks.
            }
        }
        return null;
    }

    internal static bool IsGoTestCommand(AcceptanceManifestCheck check) =>
        check.Type?.Equals("command", StringComparison.OrdinalIgnoreCase) == true &&
        !string.IsNullOrWhiteSpace(check.Command) &&
        Path.GetFileNameWithoutExtension(check.Command.Replace('\\', '/').Split('/')[^1])
            .Equals("go", StringComparison.OrdinalIgnoreCase) &&
        check.Arguments is { Count: > 0 } && check.Arguments[0] == "test";

    internal static bool Applies(AcceptanceGateEngineSettings? settings) =>
        settings?.DeclaredGoTestCheck is not null &&
        settings.MtpInvocations.Count == 0 && settings.DeclaredDotnetTestChecks.Count == 0;

    internal static bool TryResolveProject(
        string alias, AcceptanceGateEngineSettings? settings, out string project)
    {
        project = string.Empty;
        if (!Applies(settings))
            return false;
        var package = NormalizePackage(alias);
        project = PackagePattern.IsMatch(package) ? package : settings!.DeclaredGoTestCheck!.Name;
        return true;
    }

    internal static bool TryBuild(
        string request,
        AcceptanceGateEngineSettings settings,
        string worktreePath,
        out IReadOnlyList<AcceptanceManifestCheck> checks,
        out FocusedEvidenceCoverage coverage,
        out FocusedEvidenceRejection rejection)
    {
        checks = [];
        coverage = new FocusedEvidenceCoverage(TargetToChecks: []);
        rejection = new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.EmptyRequest, request, "empty evidence request");
        if (!Applies(settings))
            return false;
        var items = request.Split(';').Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
        if (items.Length == 0)
            return false;

        var declared = settings.DeclaredGoTestCheck!;
        var built = new List<AcceptanceManifestCheck>();
        var checkNames = new HashSet<string>(StringComparer.Ordinal);
        var mappings = new List<FocusedEvidenceTargetCoverage>();
        var scopedCount = 0;
        foreach (var item in items)
        {
            var separator = item.IndexOf(':');
            var package = NormalizePackage(separator < 0 ? item : item[..separator]);
            var names = separator < 0 ? [] : item[(separator + 1)..].Split('|')
                .Select(name => name.Trim())
                .Select(name => name.StartsWith("FullyQualifiedName~", StringComparison.Ordinal)
                    ? name["FullyQualifiedName~".Length..] : name)
                .ToArray();
            var scoped = PackagePattern.IsMatch(package) && HasTestFile(worktreePath, package) &&
                (separator < 0 || names.All(name => TestNamePattern.IsMatch(name)));
            AcceptanceManifestCheck check;
            if (scoped)
            {
                scopedCount++;
                names = names.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                var union = string.Join('|', names);
                check = new AcceptanceManifestCheck
                {
                    Name = FocusedPrefix + package + (separator < 0 ? "" : " " + union),
                    Type = "command",
                    Command = declared.Command,
                    TimeoutMinutes = declared.TimeoutMinutes,
                    Arguments = separator < 0
                        ? ["test", "-count=1", "-v", "./" + package]
                        : ["test", "-count=1", "-v", "-run", "^(" + union + ")$", "./" + package]
                };
            }
            else
            {
                check = new AcceptanceManifestCheck
                {
                    Name = FallbackPrefix + declared.Name,
                    Type = "command",
                    Command = declared.Command,
                    TimeoutMinutes = declared.TimeoutMinutes,
                    Arguments = declared.Arguments.Contains("-v", StringComparer.Ordinal)
                        ? declared.Arguments
                        : declared.Arguments.Take(1).Append("-v").Concat(declared.Arguments.Skip(1)).ToArray()
                };
            }
            if (checkNames.Add(check.Name))
                built.Add(check);
            mappings.Add(new FocusedEvidenceTargetCoverage(item, [check.Name]));
        }

        checks = built;
        coverage = new FocusedEvidenceCoverage(
            mappings,
            ExecutionMode: scopedCount == items.Length ? "focused" : scopedCount == 0 ? "project" : "mixed",
            ExecutionReason: scopedCount == items.Length ? "go-package-focused"
                : scopedCount == 0 ? "go-declared-check-fallback"
                : "go-package-focused+go-declared-check-fallback");
        return true;
    }

    internal static bool IsEvidenceCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("command", StringComparison.OrdinalIgnoreCase) &&
        (check.Name.StartsWith(FocusedPrefix, StringComparison.Ordinal) ||
         check.Name.StartsWith(FallbackPrefix, StringComparison.Ordinal));

    internal static IReadOnlyList<AcceptanceCheckResult> AnnotateResults(
        IReadOnlyList<AcceptanceManifestCheck> planned, IReadOnlyList<AcceptanceCheckResult> results)
    {
        var evidenceChecks = planned.Where(IsEvidenceCheck)
            .GroupBy(check => check.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return results.Select(result =>
        {
            if (!evidenceChecks.TryGetValue(result.Name, out var check))
                return result;
            var lines = (result.OutputTail ?? "").Split('\n').Select(line => line.Trim()).ToArray();
            var prefix = check.Name.StartsWith(FocusedPrefix, StringComparison.Ordinal)
                ? NormalizePackage(check.Arguments[^1]) + "." : "";
            return result with
            {
                ExecutedTestCount = lines.Count(line => line.StartsWith(PassMarker, StringComparison.Ordinal) ||
                    line.StartsWith(FailMarker, StringComparison.Ordinal)),
                FailingTestIdentities = lines.Where(line => line.StartsWith(FailMarker, StringComparison.Ordinal))
                    .Select(line => prefix + line[FailMarker.Length..].Split(' ')[0]).ToArray()
            };
        }).ToArray();
    }

    private static string NormalizePackage(string package)
    {
        var normalized = package.Replace('\\', '/').Trim();
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimEnd('/');
    }

    private static bool HasTestFile(string worktreePath, string package)
    {
        try
        {
            return Directory.EnumerateFiles(Path.Combine(worktreePath, package))
                .Any(path => path.EndsWith("_test.go", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
