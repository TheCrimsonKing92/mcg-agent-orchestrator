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

    public static VerificationPolicy Compile(
        AgentRole role,
        string goalObjective,
        string taskDescription,
        string? verificationPlan,
        IEnumerable<string> changedFiles)
    {
        var files = changedFiles.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        var impact = RepositoryTestImpactPlanner.Plan(files);
        return Compile(
            role,
            goalObjective,
            taskDescription,
            verificationPlan,
            files,
            impact);
    }

    public static VerificationPolicy Compile(
        AgentRole role,
        string goalObjective,
        string taskDescription,
        string? verificationPlan,
        IEnumerable<string> changedFiles,
        RepositoryTestImpactPlan impact)
    {
        ArgumentNullException.ThrowIfNull(impact);
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
        var requiresTests = distinct.Any(check => check.Required && check.Kind == "dotnet-test");
        var requiresHuman = distinct.Any(check => check.Required && check.Kind.StartsWith("manual", StringComparison.OrdinalIgnoreCase));
        var summary = requiresTests
            ? impact.Summary
            : "No deterministic build/test command required by current file scope.";

        return new VerificationPolicy(summary, requiresTests, requiresHuman, distinct);
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> signals) =>
        signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
