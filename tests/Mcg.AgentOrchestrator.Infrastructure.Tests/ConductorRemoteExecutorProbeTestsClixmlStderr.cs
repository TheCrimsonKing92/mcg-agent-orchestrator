using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each probe uses its own directory, injected clock and transport; no SSH or global state.
public sealed class ConductorRemoteExecutorProbeTestsClixmlStderr
{
    private const string Open = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">";
    private const string Progress = "<Obj S=\"progress\"><MS><S N=\"AV\">Preparing modules for first use.</S></MS></Obj>";

    [Xunit.Fact]
    public async Task HealthyProbe_WithProgressOnly_StoresEmptyTailAndReasons()
    {
        using var fixture = new Fixture();
        var row = await fixture.Probe(Open + Progress + Progress + "</Objs>\r\n");
        Assert.Equal("", row.StderrTail);
        Assert.Empty(row.Reasons);
        Assert.True(row.Reachable);
        Assert.Equal("Ready", row.TaskState);
        Assert.True(row.PowerOnline);
        Assert.Equal(0, row.ExitCode);
        Assert.False(row.TimedOut);
        Assert.False(File.Exists(fixture.Events));
    }

    [Xunit.Fact]
    public async Task HealthyProbe_WithProgressAndError_StoresReadableError()
    {
        using var fixture = new Fixture();
        var row = await fixture.Probe(Open + Progress +
            "<S S=\"Error\">Access is denied_x000D__x000A_</S></Objs>\r\n");
        Assert.Equal("Access is denied", row.StderrTail);
        Assert.Empty(row.Reasons);
    }

    [Xunit.Fact]
    public async Task HealthyProbe_WithLongError_BoundsDecodedTextToLast2048()
    {
        using var fixture = new Fixture();
        var error = new string(Enumerable.Range(0, 3000).Select(index => (char)('a' + index % 26)).ToArray());
        var row = await fixture.Probe(Open + Progress + "<S S=\"Error\">" + error +
            "_x000D__x000A_</S></Objs>\r\n");
        Assert.Equal(error[^2048..], row.StderrTail);
        Assert.Empty(row.Reasons);
    }

    [Xunit.Fact]
    public async Task EncodedScript_SuppressesProgressBeforeFirstCommand()
    {
        using var fixture = new Fixture();
        await fixture.Probe("");
        var args = Assert.IsType<string[]>(fixture.Arguments);
        Assert.Equal(new[] { SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
            "admin-one", "powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand" }, args[..^1]);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
        const string preference = "$ProgressPreference = 'SilentlyContinue'; ";
        Assert.StartsWith(preference, script);
        Assert.True(script.IndexOf(preference, StringComparison.Ordinal) <
            script.IndexOf("schtasks", StringComparison.Ordinal));
        Assert.Equal("$task = schtasks /query /tn mcg-executor-lane /fo csv /nh 2>$null | Select-Object -First 1; " +
            "$power = $null; try { $battery = Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus -ErrorAction Stop | Select-Object -First 1; " +
            "if ($null -ne $battery) { $power = [bool]$battery.PowerOnline } } catch {}; " +
            "[pscustomobject]@{ task = $task; powerOnline = $power } | ConvertTo-Json -Compress", script[preference.Length..]);
    }

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal string Events => Path.Combine(Root, "events.log");
        internal string[]? Arguments { get; private set; }

        internal async Task<RemoteExecutorProbeRow> Probe(string stderr)
        {
            var configuration = Path.Combine(Root, "executors.json");
            File.WriteAllText(configuration, JsonSerializer.Serialize(new
            {
                executors = new[] { new { id = "one", transport = "ssh", runnerAlias = "runner-one",
                    adminAlias = "admin-one", remoteRepository = "C:/repo/bare.git" } },
                lanes = Array.Empty<string>()
            }));
            var probe = new ConductorRemoteExecutorProbe(Root, configuration, new ManualRemoteLaneClock(),
                (args, directory, bound, _) =>
                {
                    Arguments = args;
                    Assert.Equal(Root, directory);
                    Assert.Equal(TimeSpan.FromSeconds(30), bound);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                        JsonSerializer.Serialize(new { task = "\"mcg-executor-lane\",\"N/A\",\"Ready\"", powerOnline = true }))
                        { Stderr = stderr });
                }, _ => null, new ConductEventLogWriter(Events));
            probe.Evaluate();
            Assert.NotNull(probe.CurrentProbe);
            await probe.CurrentProbe;
            var rows = RemoteExecutorReportReader.ReadProbes(
                Path.Combine(Root, ".orchestrator", RemoteExecutorProbeLedger.FileName), out var unreadable);
            Assert.Equal(0, unreadable);
            return Assert.Single(rows);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
