using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immediate fake transport, no process or ambient state.
public sealed class RemoteExecutorSlotsFileSyncTests
{
    private const string Backup = "slots.txt.20261018T101112Z.12345678.bak";
    private static readonly RemoteLaneExecutorEntry Entry = new("one", 60, "ssh", "runner", "admin", "C:/repo", "D:/runner", 10, 3, false);

    [Theory]
    [InlineData(255, false, "{}", "unreachable")]
    [InlineData(0, false, " \r\n", "unreachable")]
    [InlineData(255, true, "", "timed-out")]
    [InlineData(1, false, "{}", "remote-exit-1")]
    [InlineData(0, false, "not-json", "output-unparsable")]
    [InlineData(0, false, "{", "output-unparsable")]
    [InlineData(0, false, "{}", "output-unparsable")]
    [InlineData(0, false, """{"error":"backup-exists"}""", "remote-backup-exists")]
    [InlineData(0, false, """{"readback":"4"}""", "readback-mismatch")]
    [InlineData(0, false, """{"readback":"05"}""", "readback-mismatch")]
    [InlineData(0, false, """{"readback":5}""", "readback-mismatch")]
    public void FailureSignalsHaveDeterministicReasons(int exit, bool timedOut, string output, string reason)
    {
        var calls = 0;
        var sync = new RemoteExecutorSlotsFileSync((_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(exit, output, timedOut) { Stderr = "ssh-error" });
        }, "fixture", TimeSpan.FromSeconds(30));
        var result = sync.Sync(Entry, 5, Backup);
        Assert.Equal(1, calls);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal("ssh-error", result.ErrorTail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingTransportIsRecordedWithoutEscaping(bool asynchronous)
    {
        var sync = new RemoteExecutorSlotsFileSync((_, _, _, _) => asynchronous
            ? Task.FromException<GoalAcceptanceVerifier.CommandResult>(new IOException("fixture failure"))
            : throw new IOException("fixture failure"), "fixture", TimeSpan.FromSeconds(30));
        var result = sync.Sync(Entry, 5, Backup);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("transport-error", result.Reason);
        Assert.Equal("fixture failure", result.ErrorTail);
    }

    [Fact]
    public void SuccessUsesOneBoundedCallAndReturnsReadbackReceipt()
    {
        var calls = 0;
        var sync = new RemoteExecutorSlotsFileSync((args, directory, bound, token) =>
        {
            calls++;
            Assert.Equal("fixture", directory);
            Assert.Equal(TimeSpan.FromSeconds(30), bound);
            Assert.Equal(CancellationToken.None, token);
            Assert.Equal(new[] { SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                "admin", "powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand" }, args[..^1]);
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
            Assert.Contains("$root = 'D:/runner'", script);
            Assert.Contains("'" + Backup + "'", script);
            Assert.Contains("'slots.txt'", script);
            Assert.Contains("-Value '5' -NoNewline -Encoding ascii", script);
            Assert.Contains("slots.txt.20261018T101112Z.12345678.tmp", script);
            Assert.True(script.IndexOf("Test-Path -LiteralPath $bak", StringComparison.Ordinal) <
                script.IndexOf("Copy-Item", StringComparison.Ordinal));
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                "banner\n" + JsonSerializer.Serialize(new { previous = " 3 ", backup = Backup, readback = " 5 " })));
        }, "fixture", TimeSpan.FromSeconds(30));
        var result = sync.Sync(Entry, 5, Backup);
        Assert.Equal(1, calls);
        Assert.Equal(new RemoteSlotsSyncResult("synced", null, Backup, "3", null), result);
    }

    [Fact]
    public void ErrorTailIsBoundedAndAbsentOnSuccess()
    {
        var sync = new RemoteExecutorSlotsFileSync((_, _, _, _) => Task.FromResult(
            new GoalAcceptanceVerifier.CommandResult(255, "") { Stderr = new string('e', 2200) }), "fixture", TimeSpan.FromSeconds(30));
        Assert.Equal(new string('e', 2048), sync.Sync(Entry, 5, Backup).ErrorTail);
        sync = new((_, _, _, _) => Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
            """{"readback":"5","previous":null,"backup":null}""") { Stderr = "warning" }), "fixture", TimeSpan.FromSeconds(30));
        Assert.Equal(new RemoteSlotsSyncResult("synced", null, null, null, null), sync.Sync(Entry, 5, Backup));
    }
}
