public sealed class DispatchHostLifetimeHandoffTestsWaitMessage
{
    [Xunit.Fact]
    public void MissingLaunchedHeartbeat_ReportsStateDiagnosticTailAndLogPresence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-handoff-message", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            var heartbeatPath = Path.Combine(directory, "heartbeat.json");
            var diagnosticPath = Path.Combine(directory, "host.err.log");
            var stdoutPath = Path.Combine(directory, "out.log");
            var stderrPath = Path.Combine(directory, "err.log");
            File.WriteAllText(heartbeatPath, "{\"state\":\"starting\"}");
            File.WriteAllText(diagnosticPath, new string('x', 4100) + "known diagnostic tail");
            File.WriteAllText(stderrPath, "");

            var message = DispatchHostLifetimeHandoffTests.DescribeMissingLaunchedHeartbeat(
                heartbeatPath, "starting", diagnosticPath, stdoutPath, stderrPath);

            Assert.Contains("Launched heartbeat", message, StringComparison.Ordinal);
            Assert.Contains("identity-bound child was not observed", message, StringComparison.Ordinal);
            Assert.Contains("last-state=starting", message, StringComparison.Ordinal);
            Assert.Contains("heartbeat-tail={\"state\":\"starting\"}", message, StringComparison.Ordinal);
            Assert.Contains("host-diagnostic-tail=", message, StringComparison.Ordinal);
            Assert.Contains("known diagnostic tail", message, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 4100), message, StringComparison.Ordinal);
            Assert.Contains("stdout-log=absent", message, StringComparison.Ordinal);
            Assert.Contains("stderr-log=present", message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
