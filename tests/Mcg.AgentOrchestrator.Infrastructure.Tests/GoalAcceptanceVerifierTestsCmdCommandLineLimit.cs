using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsCmdCommandLineLimit : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public void OverLimitReportsFullyComposedLengthAndCommand()
    {
        if (!OperatingSystem.IsWindows()) return;
        var payload = new string('x', 8200);
        const string stdout = @"\\.\pipe\limit-out";
        const string stderr = @"\\.\pipe\limit-err";
        var expectedLine = $"/c \"(chcp 65001 > nul && \"git\" \"diff\" \"{payload}\") > \"{stdout}\" 2> \"{stderr}\"\"";

        var error = Assert.Throws<CommandLineTooLongException>(() =>
            GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(
                ["git", "diff", payload], Path.GetTempPath(), stdout, stderr, forceUtf8ConsoleOutput: true));

        Assert.Equal(expectedLine.Length, error.Length);
        Assert.Equal(8191, error.Limit);
        Assert.Equal("git", error.FirstArgument);
        Assert.Equal($"Windows cmd.exe command line is {expectedLine.Length} characters, which exceeds the cmd.exe limit of 8191 characters; first argument: git", error.Message);
        Assert.DoesNotContain(payload, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8190)]
    [InlineData(8191)]
    public void AtOrBelowLimitBuildsWindowsLaunch(int length)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string emptyLine = "/c \"\"git\" \"\"\"";
        var startInfo = GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(
            ["git", new string('x', length - emptyLine.Length)], Path.GetTempPath());

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Equal(length, startInfo.Arguments.Length);
    }

    [Fact]
    public void OneCharacterOverLimitIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string emptyLine = "/c \"\"git\" \"\"\"";
        var error = Assert.Throws<CommandLineTooLongException>(() =>
            GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(
                ["git", new string('x', 8192 - emptyLine.Length)], Path.GetTempPath()));
        Assert.Equal(8192, error.Length);
        Assert.Equal(8191, error.Limit);
    }

    [Fact]
    public async Task ProductionRunnerRejectsBeforeProcessStartOrPipeWait()
    {
        if (!OperatingSystem.IsWindows()) return;
        var observations = new List<AcceptanceProcessCleanupObservation>();
        var identitiesObserved = 0;
        var error = await Assert.ThrowsAsync<CommandLineTooLongException>(() =>
            GoalAcceptanceVerifier.RunProcessForTestsAsync(
                ["git", new string('x', 8200)], Path.GetTempPath(), TimeSpan.FromSeconds(30),
                cleanupObserver: observations.Add,
                commandIdentityObserver: _ => identitiesObserved++));

        Assert.True(error.Length > 8191);
        Assert.Equal(8191, error.Limit);
        Assert.Equal("git", error.FirstArgument);
        Assert.Contains(error.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Contains("8191", error.Message, StringComparison.Ordinal);
        Assert.Contains("first argument: git", error.Message, StringComparison.Ordinal);
        Assert.Empty(observations);
        Assert.Equal(0, identitiesObserved);
    }
}
