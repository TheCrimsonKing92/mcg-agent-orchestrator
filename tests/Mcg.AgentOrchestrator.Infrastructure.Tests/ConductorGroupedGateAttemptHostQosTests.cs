using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private attempt files, no console mutation and async-flow-scoped QoS enablement.
public sealed class ConductorGroupedGateAttemptHostQosTests
{
    [Fact]
    public void Run_Enabled_OptsOutBeforeGateBody()
    {
        using var fixture = new AttemptFixture(Policy(true));
        using var scope = AcceptanceChildQos.PushForTests(false);
        var calls = new List<(int, uint, uint)>();
        var bodies = 0;
        var enabledInBody = false;
        (int, uint, uint)[] callsInBody = [];
        var exit = ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath, _ =>
        {
            bodies++;
            enabledInBody = AcceptanceChildQos.Enabled;
            callsInBody = calls.ToArray();
            return null;
        }, (_, _) => new NoopRestoration(),
            (pid, control, state) => { calls.Add((pid, control, state)); return true; });

        Assert.Equal(1, bodies);
        Assert.True(enabledInBody);
        Assert.Equal([(Environment.ProcessId, 1u, 0u)], callsInBody);
        Assert.Equal(callsInBody, calls);
        fixture.AssertCompleted(exit);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("not json")]
    public void Run_DisabledOrInvalidPolicy_PreservesBaseline(string policy)
    {
        var baseline = RunDisabled(Policy(false));
        var actual = RunDisabled(policy == "false" ? Policy(false) : policy);
        Assert.Equal(baseline, actual);
    }

    [Fact]
    public void Run_SetterThrows_StillEnablesAndCompletesGate()
    {
        using var fixture = new AttemptFixture(Policy(true));
        using var scope = AcceptanceChildQos.PushForTests(false);
        var bodies = 0;
        var calls = 0;
        var exit = ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath, _ =>
        {
            bodies++;
            Assert.True(AcceptanceChildQos.Enabled);
        }, (_, _) => new NoopRestoration(), (_, _, _) =>
        {
            calls++;
            throw new IOException("QoS unavailable.");
        });
        Assert.Equal(1, calls);
        Assert.Equal(1, bodies);
        fixture.AssertCompleted(exit);
    }

    [Fact]
    public void Run_AlreadyCompleted_DoesNotOptOutOrRunBody()
    {
        using var fixture = new AttemptFixture(Policy(true));
        using var scope = AcceptanceChildQos.PushForTests(false);
        File.WriteAllText(fixture.Attempt.ExitCodePath, "0");
        var bodies = 0;
        var calls = 0;
        var exit = ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath, _ => { bodies++; },
            (_, _) => new NoopRestoration(), (_, _, _) => { calls++; return true; });
        Assert.Equal(0, exit);
        Assert.Equal(0, calls);
        Assert.Equal(0, bodies);
        Assert.False(AcceptanceChildQos.Enabled);
    }

    private static (int Exit, string Result, string ExitFile) RunDisabled(string policy)
    {
        using var fixture = new AttemptFixture(policy);
        using var scope = AcceptanceChildQos.PushForTests(false);
        var bodies = 0;
        var enabledInBody = true;
        var calls = 0;
        var exit = ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath, _ =>
        {
            bodies++;
            enabledInBody = AcceptanceChildQos.Enabled;
        }, (_, _) => new NoopRestoration(), (_, _, _) => { calls++; return true; });
        Assert.Equal(1, bodies);
        Assert.False(enabledInBody);
        Assert.False(AcceptanceChildQos.Enabled);
        Assert.Equal(0, calls);
        fixture.AssertCompleted(exit);
        return (exit, File.ReadAllText(fixture.Attempt.ResultPath), File.ReadAllText(fixture.Attempt.ExitCodePath));
    }

    private static string Policy(bool enabled) =>
        (ConductorAutonomyPolicy.Conservative with { AcceptanceChildrenOptOutExecutionThrottling = enabled }).ToJson();

    private sealed class NoopRestoration : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class AttemptFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "grouped-qos-" + Guid.NewGuid().ToString("N"));
        internal ConductorGroupedGateAttempt Attempt { get; }

        internal AttemptFixture(string policy)
        {
            Attempt = new("host-attempt", "cohort", [], "main", "tree", "manifest", "host-identity",
                DateTimeOffset.UnixEpoch, 0, 91001,
                Path.Combine(root, "attempt.json"), Path.Combine(root, "result.json"), Path.Combine(root, "exit"),
                Path.Combine(root, "out.log"), Path.Combine(root, "err.log"), root, policy);
            ConductorGroupedGateAttemptCoordinator.Save(Attempt);
        }

        internal void AssertCompleted(int exit)
        {
            Assert.Equal(0, exit);
            Assert.Equal("0", File.ReadAllText(Attempt.ExitCodePath));
            using var result = JsonDocument.Parse(File.ReadAllText(Attempt.ResultPath));
            Assert.Equal("host-identity", result.RootElement.GetProperty("IdentityValue").GetString());
            Assert.Equal("completed", result.RootElement.GetProperty("Status").GetString());
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
