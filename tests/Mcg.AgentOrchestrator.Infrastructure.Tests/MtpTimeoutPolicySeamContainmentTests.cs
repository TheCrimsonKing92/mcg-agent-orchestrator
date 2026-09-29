using Mcg.AgentOrchestrator.Infrastructure;

public sealed class MtpTimeoutPolicySeamContainmentTests
{
    [Xunit.Fact]
    public void TestOnlyVariablesStayInsidePolicyHelperAndOutsideAcceptanceChildren()
    {
        var names = new[]
        {
            "MCG_MTP_TEST_TIMEOUT_SIGNAL_PATH",
            "MCG_MTP_TEST_GRACEFUL_EXIT_SECONDS"
        };
        var root = MtpTestRunnerScriptTests.RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "scripts", "MtpTestRunner.psm1"));
        const string declaration = "function Get-MtpTimeoutPolicy {";
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Xunit.Assert.True(start >= 0, "Timeout policy helper is missing.");
        var openingBrace = start + declaration.Length - 1;
        var depth = 0;
        var end = -1;
        for (var index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            if (source[index] == '}' && --depth == 0)
            {
                end = index;
                break;
            }
        }
        Xunit.Assert.True(end > openingBrace, "Timeout policy helper has no closing brace.");

        foreach (var name in names)
        {
            Xunit.Assert.False(GoalAcceptanceVerifier.IsHermeticVerificationEnvironmentVariable(name));
            var occurrence = source.IndexOf(name, StringComparison.Ordinal);
            Xunit.Assert.True(occurrence >= start && occurrence < end, $"{name} is missing from the policy helper.");
            while (occurrence >= 0)
            {
                Xunit.Assert.InRange(occurrence, start, end - 1);
                occurrence = source.IndexOf(name, occurrence + name.Length, StringComparison.Ordinal);
            }
        }
    }
}
