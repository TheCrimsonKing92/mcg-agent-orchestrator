namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryChangeClassifierOwnerProtectedTests
{
    [Xunit.Fact]
    public void ReportsChangedJsonPathsAndPolicyName()
    {
        const string trusted = """{"checks":[{"command":"old"}],"engine":{"enabled":true}}""";
        const string candidate = """{"checks":[{"command":"new"}],"engine":{"enabled":true}}""";

        Assert.Equal(["checks[0].command"],
            RepositoryChangeClassifier.DescribeJsonChanges(trusted, candidate));
        Assert.True(RepositoryChangeClassifier.IsOwnerProtectedPolicyPath("config/conductor-policy.json"));
        Assert.True(RepositoryChangeClassifier.IsOwnerProtectedPolicyPath("other\\Conductor-Policy.JSON"));
        Assert.False(RepositoryChangeClassifier.IsOwnerProtectedPolicyPath("config/other-policy.json"));
    }

    [Xunit.Fact]
    public void ReportsAddedAndRemovedFields()
    {
        Assert.Equal(["autonomy.maxRetries"], RepositoryChangeClassifier.DescribeJsonChanges(
            null, """{"autonomy":{"maxRetries":3}}"""));
        Assert.Equal(["checks[1]"], RepositoryChangeClassifier.DescribeJsonChanges(
            """{"checks":["a","b"]}""", """{"checks":["a"]}"""));
    }

    [Xunit.Fact]
    public void DuplicateJsonKeyIsReportedWithoutThrowing()
    {
        var fields = RepositoryChangeClassifier.DescribeJsonChanges(
            """{"checks":[{"command":"old"}]}""",
            """{"checks":[{"command":"old","command":"new"}]}""");

        Assert.Contains("checks[0].command (duplicate key)", fields);
        Assert.Contains("checks[0].command", fields);
    }
}
