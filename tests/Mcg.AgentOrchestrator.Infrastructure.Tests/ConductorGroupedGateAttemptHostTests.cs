using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorGroupedGateAttemptHostTests
{
    private const string FailureMessage = "distinctive grouped gate body failure";

    [Fact(DisplayName = "A throwing gate publishes the original exception and failure artifacts")]
    public void ThrowingGatePublishesOriginalFailure()
    {
        using var fixture = new AttemptFixture();
        File.WriteAllText(fixture.Attempt.StderrPath, "earlier gate diagnostic\n");
        var exitCode = -1;

        var exception = Record.Exception(() => exitCode = ConductorGroupedGateAttemptHost.Run(
            fixture.Attempt.MetadataPath, _ => throw new InvalidOperationException(FailureMessage),
            (_, _) => new NoopRestoration()));

        Assert.Null(exception);
        Assert.Equal(1, exitCode);
        var stderr = File.ReadAllText(fixture.Attempt.StderrPath);
        Assert.Contains("earlier gate diagnostic", stderr);
        Assert.Contains("System.InvalidOperationException", stderr);
        Assert.Contains(FailureMessage, stderr);
        AssertFailedResult(fixture.Attempt);
        Assert.Equal("1", File.ReadAllText(fixture.Attempt.ExitCodePath).Trim());
    }

    [Fact(DisplayName = "A completing gate preserves the completed result and zero exit code")]
    public void CompletingGatePublishesSuccess()
    {
        using var fixture = new AttemptFixture();
        var exitCode = ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath,
            _ => { }, (_, _) => new NoopRestoration());

        Assert.Equal(0, exitCode);
        Assert.Equal(JsonSerializer.Serialize(new { fixture.Attempt.IdentityValue, Status = "completed" }),
            File.ReadAllText(fixture.Attempt.ResultPath));
        Assert.Equal("0", File.ReadAllText(fixture.Attempt.ExitCodePath).Trim());
    }

    [Theory(DisplayName = "A failed publication still attempts the remaining failure artifacts")]
    [InlineData("stderr")]
    [InlineData("result")]
    [InlineData("exit")]
    public void FailurePublicationsAreIndependent(string failedWrite)
    {
        using var fixture = new AttemptFixture();
        if (failedWrite == "result") Directory.CreateDirectory(fixture.Attempt.ResultPath);
        if (failedWrite == "exit") Directory.CreateDirectory(fixture.Attempt.ExitCodePath);
        var exitCode = -1;

        var exception = Record.Exception(() => exitCode = ConductorGroupedGateAttemptHost.Run(
            fixture.Attempt.MetadataPath, _ => throw new InvalidOperationException(FailureMessage),
            (_, stderr) =>
            {
                if (failedWrite == "stderr") stderr.Dispose();
                return new NoopRestoration();
            }));

        Assert.Null(exception);
        Assert.Equal(1, exitCode);
        if (failedWrite != "stderr")
            Assert.Contains(FailureMessage, File.ReadAllText(fixture.Attempt.StderrPath));
        if (failedWrite != "result") AssertFailedResult(fixture.Attempt);
        if (failedWrite != "exit")
            Assert.Equal("1", File.ReadAllText(fixture.Attempt.ExitCodePath).Trim());
    }

    [Fact(DisplayName = "Failure before stdout opens still publishes stderr and failure artifacts")]
    public void WriterOpenFailurePublishesFailure()
    {
        using var fixture = new AttemptFixture();
        Directory.CreateDirectory(fixture.Attempt.StdoutPath);
        var gateCalled = false;
        var exitCode = -1;

        var exception = Record.Exception(() => exitCode = ConductorGroupedGateAttemptHost.Run(
            fixture.Attempt.MetadataPath, _ => gateCalled = true, (_, _) => new NoopRestoration()));

        Assert.Null(exception);
        Assert.Equal(1, exitCode);
        Assert.False(gateCalled);
        using var result = JsonDocument.Parse(File.ReadAllText(fixture.Attempt.ResultPath));
        Assert.Equal(fixture.Attempt.IdentityValue, result.RootElement.GetProperty("IdentityValue").GetString());
        Assert.Equal("failed", result.RootElement.GetProperty("Status").GetString());
        var stderr = File.ReadAllText(fixture.Attempt.StderrPath);
        Assert.Contains(result.RootElement.GetProperty("ErrorType").GetString()!, stderr);
        Assert.Contains(result.RootElement.GetProperty("Error").GetString()!, stderr);
        Assert.Equal("1", File.ReadAllText(fixture.Attempt.ExitCodePath).Trim());
    }

    [Fact(DisplayName = "Restoration runs while the host writers are still open")]
    public void RestorationPrecedesWriterDisposal()
    {
        using var fixture = new AttemptFixture();
        var restored = false;
        var exitCode = ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath,
            _ => throw new InvalidOperationException(FailureMessage),
            (stdout, stderr) => new CallbackRestoration(() =>
            {
                stdout.WriteLine("stdout restored");
                stderr.WriteLine("stderr restored");
                restored = true;
            }));

        Assert.Equal(1, exitCode);
        Assert.True(restored);
        Assert.Contains("stdout restored", File.ReadAllText(fixture.Attempt.StdoutPath));
        Assert.Contains("stderr restored", File.ReadAllText(fixture.Attempt.StderrPath));
        using var stdoutLease = File.Open(fixture.Attempt.StdoutPath, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        using var stderrLease = File.Open(fixture.Attempt.StderrPath, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
    }

    [Fact(DisplayName = "A restoration failure after a completed body publishes a failed result")]
    public void RestorationFailurePublishesFailure()
    {
        using var fixture = new AttemptFixture();
        var exitCode = -1;

        var exception = Record.Exception(() => exitCode = ConductorGroupedGateAttemptHost.Run(
            fixture.Attempt.MetadataPath, _ => { }, (_, _) => new CallbackRestoration(
                () => throw new InvalidOperationException(FailureMessage))));

        Assert.Null(exception);
        Assert.Equal(1, exitCode);
        Assert.Contains(FailureMessage, File.ReadAllText(fixture.Attempt.StderrPath));
        AssertFailedResult(fixture.Attempt);
        Assert.Equal("1", File.ReadAllText(fixture.Attempt.ExitCodePath).Trim());
    }

    private static void AssertFailedResult(ConductorGroupedGateAttempt attempt)
    {
        using var result = JsonDocument.Parse(File.ReadAllText(attempt.ResultPath));
        Assert.Equal(attempt.IdentityValue, result.RootElement.GetProperty("IdentityValue").GetString());
        Assert.Equal("failed", result.RootElement.GetProperty("Status").GetString());
        Assert.Equal(nameof(InvalidOperationException), result.RootElement.GetProperty("ErrorType").GetString());
        Assert.Equal(FailureMessage, result.RootElement.GetProperty("Error").GetString());
    }

    private sealed class NoopRestoration : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class CallbackRestoration(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private sealed class AttemptFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"grouped-host-{Guid.NewGuid():N}");
        internal ConductorGroupedGateAttempt Attempt { get; }

        internal AttemptFixture()
        {
            Attempt = new ConductorGroupedGateAttempt("host-attempt", "cohort", [], "main", "tree",
                "manifest", "host-identity", DateTimeOffset.UnixEpoch, 0, 91001,
                Path.Combine(_root, "attempt.json"), Path.Combine(_root, "result.json"),
                Path.Combine(_root, "exit"), Path.Combine(_root, "out.log"),
                Path.Combine(_root, "err.log"), _root, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(Attempt);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
