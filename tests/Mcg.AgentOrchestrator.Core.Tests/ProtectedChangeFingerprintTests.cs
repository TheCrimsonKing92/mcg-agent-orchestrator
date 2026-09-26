namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class ProtectedChangeFingerprintTests
{
    private const string Path = "config/acceptance-manifest.json";

    [Xunit.Fact]
    public void IdenticalFieldChangesHaveStableVersionedFingerprint()
    {
        var first = Fingerprint("""{"checks":{"one":1,"two":2}}""",
            """{"checks":{"one":3,"two":2}}""");
        var reordered = Fingerprint("""{ "checks": { "two":2, "one":1 } }""",
            """{"checks":{"two":2,"one":3}}""");

        Assert.StartsWith("v1:", first);
        Assert.Equal(first, reordered);
        Assert.Equal(["checks.one"], RepositoryChangeClassifier.DescribeJsonChanges(
            """{"checks":{"one":1,"two":2}}""", """{"checks":{"one":3,"two":2}}"""));
    }

    [Xunit.Fact]
    public void ValuesKindsAndFileMembershipChangeFingerprint()
    {
        const string trusted = """{"checks":{"one":1}}""";
        var changed = Fingerprint(trusted, """{"checks":{"one":2}}""");
        Assert.NotEqual(changed, Fingerprint(trusted, """{"checks":{"one":3}}"""));
        Assert.NotEqual(changed, Fingerprint(trusted, """{"checks":{"one":2,"two":4}}"""));
        Assert.NotEqual(changed, Fingerprint(trusted, """{"checks":{}}"""));
        Assert.NotEqual(changed, RepositoryChangeClassifier.ComputeOwnerProtectedChangeFingerprint([
            (Path, trusted, """{"checks":{"one":2}}"""),
            ("config/conductor-policy.json", """{"x":1}""", """{"x":2}""")
        ]));
    }

    private static string? Fingerprint(string trusted, string candidate) =>
        RepositoryChangeClassifier.ComputeOwnerProtectedChangeFingerprint([(Path, trusted, candidate)]);
}
