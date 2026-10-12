using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its directory and runner; the probe counter is scoped.
public sealed class MaterializationScriptedGitRunnerTests
{
    [Xunit.Fact]
    public void MatchingCalls_ReturnResultsInOrderAndConsumeScript()
    {
        var first = new GitCli.GitResult(0, "first result", string.Empty);
        var second = new GitCli.GitResult(19, "second result", "refused", DrainTimedOut: true);
        var scripted = new MaterializationScriptedGitRunner(
        [
            (["status"], first),
            (["ls-files", "--eol", "-z"], second)
        ]);

        Assert.Equal(first, scripted.Runner("first-directory", ["status"]));
        Assert.Equal(second, scripted.Runner("other-directory", ["ls-files", "--eol", "-z"]));
        Assert.Equal(2, scripted.CallCount);
        scripted.AssertConsumed();
    }

    [Xunit.Theory]
    [Xunit.InlineData("case")]
    [Xunit.InlineData("order")]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("extra")]
    public void DifferentArguments_ThrowWithoutConsumingEntry(string mismatch)
    {
        string[] arguments = mismatch switch
        {
            "case" => ["STATUS", "--porcelain=v1"],
            "order" => ["--porcelain=v1", "status"],
            "missing" => ["status"],
            "extra" => ["status", "--porcelain=v1", "-z"],
            _ => throw new InvalidOperationException($"Unknown mismatch '{mismatch}'.")
        };
        var expected = new GitCli.GitResult(0, "expected result", string.Empty);
        var scripted = new MaterializationScriptedGitRunner(
            [(["status", "--porcelain=v1"], expected)]);

        var exception = Assert.Throws<InvalidOperationException>(() => scripted.Run("fixture-directory", arguments));

        Assert.Contains("call 0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fixture-directory", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"[{string.Join(" ", arguments)}]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("expected [status --porcelain=v1]", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, scripted.CallCount);
        Assert.Equal(expected, scripted.Run("fixture-directory", ["status", "--porcelain=v1"]));
        scripted.AssertConsumed();
    }

    [Xunit.Fact]
    public void CallAfterLastEntry_ThrowsWithOffendingCall()
    {
        var scripted = new MaterializationScriptedGitRunner(
            [(["status"], new GitCli.GitResult(0, string.Empty, string.Empty))]);
        scripted.Run("fixture-directory", ["status"]);
        scripted.AssertConsumed();

        var exception = Assert.Throws<InvalidOperationException>(
            () => scripted.Run("fixture-directory", ["unexpected", "argument"]));

        Assert.Contains("call 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[unexpected argument]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("script exhausted", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, scripted.CallCount);
    }

    [Xunit.Fact]
    public void UnrequestedEntry_AssertConsumedThrowsWithRemainingArguments()
    {
        var scripted = new MaterializationScriptedGitRunner(
        [
            (["status"], new GitCli.GitResult(0, string.Empty, string.Empty)),
            (["ls-files", "--eol", "-z"], new GitCli.GitResult(0, string.Empty, string.Empty))
        ]);
        scripted.Run("fixture-directory", ["status"]);

        var exception = Assert.Throws<InvalidOperationException>(scripted.AssertConsumed);

        Assert.Contains("1 unconsumed entries", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[ls-files --eol -z]", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CallerMutatesEntries_OriginalArgumentsAndOrderArePreserved()
    {
        string[] arguments = ["status"];
        var expected = new GitCli.GitResult(0, "original result", string.Empty);
        var entries = new List<(string[], GitCli.GitResult)> { (arguments, expected) };
        var scripted = new MaterializationScriptedGitRunner(entries);
        arguments[0] = "changed";
        entries.Clear();

        Assert.Equal(expected, scripted.Run("fixture-directory", ["status"]));
        scripted.AssertConsumed();
    }

    [Xunit.Theory]
    [Xunit.InlineData("drain-timeout", 3, "output drain timed out")]
    [Xunit.InlineData("scan-failure", 2, "exit=19")]
    [Xunit.InlineData("malformed-receipt", 2, "malformed receipt")]
    public void FailClosedScenario_UsesOnlyPlainFixtureAndExpectedScript(
        string scenarioName, int expectedCalls, string expectedDiagnostic)
    {
        using var probes = InfrastructureTestSupport.CountGitProbeLaunches();
        using var scenario = scenarioName switch
        {
            "drain-timeout" => MaterializationScriptedScenarios.DrainTimeout(),
            "scan-failure" => MaterializationScriptedScenarios.ScanFailure(),
            "malformed-receipt" => MaterializationScriptedScenarios.MalformedReceipt(),
            _ => throw new InvalidOperationException($"Unknown scenario '{scenarioName}'.")
        };
        var bytesBefore = File.ReadAllBytes(scenario.FixturePath);
        Assert.Equal(Encoding.UTF8.GetBytes("alpha\r\nbeta\r\n"), bytesBefore);
        Assert.Equal(scenario.FixturePath, Assert.Single(Directory.EnumerateFileSystemEntries(scenario.Directory)));
        var goalId = GoalId.New();

        var result = GoalWorktrees.ValidatePostRebaseMaterialization(
            scenario.Directory, GoalWorktrees.BranchName(goalId), "main", goalId, scenario.Runner.Runner);

        Assert.Equal(GoalWorktreeRebaseStatus.IncompleteMaterialization, result.Status);
        Assert.Contains(expectedDiagnostic, result.Message, StringComparison.Ordinal);
        Assert.Equal(bytesBefore, File.ReadAllBytes(scenario.FixturePath));
        Assert.Null(result.PreimageDirectory);
        Assert.Equal("fixture.txt", Path.GetFileName(Assert.Single(Directory.EnumerateFileSystemEntries(scenario.Directory))));
        Assert.False(Directory.Exists(Path.Combine(scenario.Directory, ".git")));
        Assert.False(File.Exists(Path.Combine(scenario.Directory, ".git")));
        Assert.Equal(0, probes.Count);
        Assert.Equal(expectedCalls, scenario.ScriptedCallCount);
        Assert.Equal(expectedCalls, scenario.Runner.CallCount);
        scenario.Runner.AssertConsumed();
    }
}
