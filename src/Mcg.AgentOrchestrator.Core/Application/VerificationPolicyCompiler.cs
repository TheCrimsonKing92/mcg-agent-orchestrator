namespace Mcg.AgentOrchestrator.Core;

public sealed record VerificationPolicyCheck(
    string Name,
    string Kind,
    bool Required,
    string CommandLine,
    string Reason);

public sealed record VerificationPolicy(
    string Summary,
    bool RequiresTests,
    bool RequiresHumanReview,
    IReadOnlyList<VerificationPolicyCheck> Checks);

public static class VerificationPolicyCompiler
{
    private static readonly string[] HumanReviewSignals =
    [
        "auth",
        "credential",
        "migration",
        "permission",
        "policy",
        "rollback",
        "sandbox",
        "secret",
        "security",
        "token"
    ];

    private static readonly string[] BrowserSignals =
    [
        "browser",
        "dashboard ui",
        "e2e",
        "end-to-end",
        "playwright",
        "screenshot",
        "ui flow"
    ];

    private static readonly string[] FrontEndExtensions =
    [
        ".tsx",
        ".ts",
        ".css",
        ".html",
        ".vue"
    ];

    public static VerificationPolicy Compile(
        AgentRole role,
        string goalObjective,
        string taskDescription,
        string? verificationPlan,
        IEnumerable<string> changedFiles)
    {
        var files = changedFiles.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        var impact = RepositoryTestImpactPlanner.Plan(files);
        var text = $"{goalObjective}\n{taskDescription}\n{verificationPlan}".ToLowerInvariant();
        var checks = new List<VerificationPolicyCheck>();

        foreach (var check in impact.Checks)
        {
            checks.Add(new VerificationPolicyCheck(
                check.Name,
                check.Command.Count == 0 ? "no-op" : "dotnet-test",
                check.Command.Count > 0,
                check.CommandLine,
                check.Reason));
        }

        if (role is AgentRole.Tester or AgentRole.Reviewer)
        {
            checks.Add(new VerificationPolicyCheck(
                "worker evidence review",
                "manual-evidence",
                true,
                "(manual)",
                "Tester and Reviewer roles must inspect worker output, verification records, and blockers before trusting task status."));
        }

        if (ContainsAny(text, BrowserSignals) && HasFrontEndAsset(files))
        {
            checks.Add(new VerificationPolicyCheck(
                "dashboard browser smoke",
                "browser-smoke",
                true,
                ".\\scripts\\Run-DashboardBrowserScript.ps1",
                "Task text references dashboard UI, browser automation, screenshots, or end-to-end smoke coverage."));
        }

        if (ContainsAny(text, HumanReviewSignals) ||
            impact.RequiresBroadVerification)
        {
            checks.Add(new VerificationPolicyCheck(
                "human risk review",
                "manual-risk-review",
                true,
                "(manual)",
                impact.RequiresBroadVerification
                    ? "Changed file scope requires broad verification or touches shared infrastructure."
                    : "Task text contains policy, security, migration, rollback, or permission risk signals."));
        }

        var distinct = checks
            .GroupBy(check => $"{check.Kind}:{check.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var requiresTests = distinct.Any(check => check.Required && check.Kind is "dotnet-test" or "browser-smoke");
        var requiresHuman = distinct.Any(check => check.Required && check.Kind.StartsWith("manual", StringComparison.OrdinalIgnoreCase));
        var summary = requiresTests
            ? impact.Summary
            : "No deterministic build/test command required by current file scope.";

        return new VerificationPolicy(summary, requiresTests, requiresHuman, distinct);
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> signals) =>
        signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static bool HasFrontEndAsset(IReadOnlyList<string> files) =>
        files.Any(file =>
        {
            var normalized = file.Replace('\\', '/');
            return FrontEndExtensions.Any(ext => normalized.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                || normalized.StartsWith("src/Mcg.AgentOrchestrator.Dashboard/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "scripts/Run-DashboardBrowserScript.ps1", StringComparison.OrdinalIgnoreCase);
        });
}
