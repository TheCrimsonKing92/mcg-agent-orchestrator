using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = ProviderHoldAgentSubstitutionTests.Scenario;

// Parallel-safe: each fixture owns its catalog directory and injected sinks/readers.
public sealed class AgentRosterReloaderTests
{
    [Xunit.Fact]
    public void MissingAtStartup_StillMissing_DoesNotReadOrEmit()
    {
        using var fixture = new Fixture(writeInitial: false);
        var reloader = fixture.Create();

        Assert.Equal(AgentRosterRefreshKind.Unchanged, reloader.Refresh().Kind);
        Assert.Equal(0, fixture.Reads);
        Assert.Empty(fixture.Lines);
        Assert.Equal(fixture.Initial, reloader.View.ToArray());
    }

    [Xunit.Fact]
    public void MembershipAndStatuses_Changed_EmitOrderedFields()
    {
        using var fixture = new Fixture();
        var reloader = fixture.Create();
        fixture.Write([fixture.Initial[1] with { Status = AgentStatus.Offline },
            fixture.Initial[0] with { Status = AgentStatus.Busy },
            fixture.Agent("z-added"), fixture.Agent("a-added")]);

        Assert.Equal(AgentRosterRefreshKind.Reloaded, reloader.Refresh().Kind);
        Assert.Equal("AGENT_ROSTER_RELOADED agents=4 changed=a-tester:Available->Busy,z-tester:Available->Offline added=a-added,z-added removed=none",
            Assert.Single(fixture.Lines));
        fixture.Write([fixture.Agent("z-added"), fixture.Agent("a-added")]);
        Assert.Equal(AgentRosterRefreshKind.Reloaded, reloader.Refresh().Kind);
        Assert.Equal("AGENT_ROSTER_RELOADED agents=2 changed=none added=none removed=a-tester,z-tester", fixture.Lines[1]);
    }

    [Xunit.Theory]
    [Xunit.InlineData("a-tester")]
    [Xunit.InlineData("A-TESTER")]
    public void Refresh_DuplicateStartupIds_ReloadsUsingFirstStatus(string duplicateId)
    {
        using var fixture = new Fixture();
        fixture.Write([fixture.Initial[0],
            fixture.Initial[0] with { Id = new AgentId(duplicateId), Status = AgentStatus.Busy },
            fixture.Initial[1]]);
        var initial = AgentCatalogStore.Load(fixture.Path).Agents;
        Assert.Equal(3, initial.Count);
        var reloader = new AgentRosterReloader(fixture.Path, initial, fixture.Lines.Add);
        var next = new[] { fixture.Initial[0] with { Status = AgentStatus.Offline }, fixture.Initial[1] };
        fixture.Write(next);

        Assert.Equal(AgentRosterRefreshKind.Reloaded, reloader.Refresh().Kind);
        Assert.Equal(next, reloader.View.ToArray());
        Assert.Equal("AGENT_ROSTER_RELOADED agents=2 changed=a-tester:Available->Offline added=none removed=none",
            Assert.Single(fixture.Lines));
        Assert.Equal(AgentRosterRefreshKind.Unchanged, reloader.Refresh().Kind);
        Assert.Single(fixture.Lines);
    }

    [Xunit.Fact]
    public void SnapshotSwap_ExistingEnumerator_KeepsOldSnapshot()
    {
        using var fixture = new Fixture();
        var reloader = fixture.Create();
        using var enumerator = reloader.View.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var observed = new List<AgentDefinition> { enumerator.Current };
        var next = fixture.Initial[1] with { Status = AgentStatus.Offline };
        fixture.Write([next]);

        Assert.Equal(AgentRosterRefreshKind.Reloaded, reloader.Refresh().Kind);
        while (enumerator.MoveNext()) observed.Add(enumerator.Current);

        Assert.Equal(fixture.Initial, observed.ToArray());
        Assert.Equal(1, reloader.View.Count);
        Assert.Equal(next, reloader.View[0]);
    }

    [Xunit.Fact]
    public void IdenticalRejectedBytes_NewerStamp_DoNotEmitAgain()
    {
        using var fixture = new Fixture();
        var reloader = fixture.Create();
        fixture.WriteText("{");
        Assert.Equal(AgentRosterRefreshKind.Rejected, reloader.Refresh().Kind);
        fixture.WriteText("{");

        Assert.Equal(AgentRosterRefreshKind.Unchanged, reloader.Refresh().Kind);
        Assert.Single(fixture.Lines);
        fixture.Write([fixture.Initial[0] with { Status = AgentStatus.Offline }]);
        Assert.Equal(AgentRosterRefreshKind.Reloaded, reloader.Refresh().Kind);
        fixture.WriteText("{");
        Assert.Equal(AgentRosterRefreshKind.Rejected, reloader.Refresh().Kind);
        Assert.Equal(3, fixture.Lines.Count);
    }

    [Xunit.Fact]
    public void TransientReadError_UnchangedStamp_RetriesWithoutRepeatedEvent()
    {
        using var fixture = new Fixture();
        var locked = false;
        var attempts = 0;
        var reloader = fixture.Create(path =>
        {
            attempts++;
            if (locked) throw new IOException("editor \"lock\"\r\nheld");
            return File.ReadAllBytes(path);
        });
        fixture.Write([fixture.Initial[0] with { Status = AgentStatus.Offline }]);
        locked = true;

        Assert.Equal(AgentRosterRefreshKind.Rejected, reloader.Refresh().Kind);
        Assert.Equal(AgentRosterRefreshKind.Rejected, reloader.Refresh().Kind);
        Assert.Equal(3, attempts);
        Assert.Equal("AGENT_ROSTER_RELOAD_REJECTED error=\"editor 'lock'  held\" kept_agents=2", Assert.Single(fixture.Lines));
        Assert.Equal(fixture.Initial, reloader.View.ToArray());
        locked = false;
        Assert.Equal(AgentRosterRefreshKind.Reloaded, reloader.Refresh().Kind);
        Assert.Equal(4, attempts);
    }

    [Xunit.Fact]
    public void ThrowingSink_Refresh_KeepsSnapshotAndRetries()
    {
        using var fixture = new Fixture();
        var fail = true;
        var reloader = new AgentRosterReloader(fixture.Path, fixture.Initial, line =>
        {
            if (fail) throw new InvalidOperationException("sink unavailable");
            fixture.Lines.Add(line);
        });
        fixture.Write([fixture.Initial[0] with { Status = AgentStatus.Offline }]);

        Assert.Equal(AgentRosterRefreshKind.Rejected, reloader.Refresh().Kind);
        Assert.Equal(fixture.Initial, reloader.View.ToArray());
        fail = false;
        Assert.Equal(AgentRosterRefreshKind.Reloaded, reloader.Refresh().Kind);
        Assert.Single(fixture.Lines);
        Assert.Equal(AgentStatus.Offline, reloader.View[0].Status);
    }

    [Xunit.Fact]
    public void Utf8Bom_NonStatusChange_ReloadsWithEmptyStatusFields()
    {
        using var fixture = new Fixture();
        var reloader = fixture.Create();
        var next = fixture.Initial.Select(agent => agent with { Name = "renamed" }).ToArray();
        fixture.Write(next);
        var json = File.ReadAllText(fixture.Path);
        File.WriteAllText(fixture.Path, json, new UTF8Encoding(true));
        File.SetLastWriteTimeUtc(fixture.Path, Scenario.ObservedAt.UtcDateTime.AddHours(1));

        Assert.Equal(AgentRosterRefreshKind.Reloaded, reloader.Refresh().Kind);
        Assert.Equal(next, reloader.View.ToArray());
        Assert.Equal("AGENT_ROSTER_RELOADED agents=2 changed=none added=none removed=none", Assert.Single(fixture.Lines));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = SharedTestSupport.CreateTempDirectory();
        private int _write;
        internal string Path => System.IO.Path.Combine(_root, "agents.json");
        internal AgentDefinition[] Initial { get; }
        internal List<string> Lines { get; } = [];
        internal int Reads;

        internal Fixture(bool writeInitial = true)
        {
            Initial = [Agent("a-tester"), Agent("z-tester")];
            if (writeInitial)
            {
                Write(Initial);
                // Match the normalized startup roster that the conduct loop supplies.
                Initial = AgentCatalogStore.Load(Path).Agents.ToArray();
            }
        }

        internal AgentDefinition Agent(string id) => Scenario.Agent(id, "OpenAI", "codex-cli", "gpt-6.1-sol");
        internal AgentRosterReloader Create(Func<string, byte[]>? reader = null) => new(Path, Initial, Lines.Add, path =>
        {
            Reads++;
            return (reader ?? File.ReadAllBytes)(path);
        });
        internal void Write(AgentDefinition[] agents)
        {
            AgentCatalogStore.Save(Path, new AgentCatalog(agents));
            File.SetLastWriteTimeUtc(Path, Scenario.ObservedAt.UtcDateTime.AddMinutes(++_write));
        }
        internal void WriteText(string text)
        {
            File.WriteAllText(Path, text);
            File.SetLastWriteTimeUtc(Path, Scenario.ObservedAt.UtcDateTime.AddMinutes(++_write));
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
