using System.Globalization;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local path-only fixtures.
// Expected strings derive from ConsoleViews.Configuration.cs at base 618b883c4.
public sealed class WorkspaceConfigurationTextViewCharacterizationTests
{
    [Fact]
    public void PrintTenant_DefaultWorkspace_PreservesExactText()
    {
        var workspace = Workspace("default", false, "repo");

        var actual = Capture(() => WorkspaceConfigurationTextView.PrintTenant(workspace));

        Assert.Equal(string.Join(Environment.NewLine,
            "Tenant: default", "Tenant scoped: False", "State: repo/state.db",
            "Agents: repo/agents.json", "Workers: repo/workers.json",
            "Continuations: repo/continuations.json", ""), actual);
    }

    [Fact]
    public void PrintTenant_ScopedWorkspace_PreservesExactText()
    {
        var workspace = Workspace("team blue", true, "tenant space");

        var actual = Capture(() => WorkspaceConfigurationTextView.PrintTenant(workspace));

        Assert.Equal(string.Join(Environment.NewLine,
            "Tenant: team blue", "Tenant scoped: True", "State: tenant space/state.db",
            "Agents: tenant space/agents.json", "Workers: tenant space/workers.json",
            "Continuations: tenant space/continuations.json", ""), actual);
    }

    [Fact]
    public void PrintArchitecture_EmptyLists_PreservesExactText()
    {
        var report = new DistributedArchitectureReport("default", false,
            "repo/state.db", "repo/agents.json", "repo/workers.json", "prompts", "logs",
            "repo/continuations.json", "repo/execute", "SQLite", "none", [], [], [], 0, 0, 0, 0);

        var actual = Capture(() => WorkspaceConfigurationTextView.PrintArchitecture(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "Architecture:", "  tenant: default scoped=False", "  state: repo/state.db",
            "  agents: repo/agents.json", "  workers: repo/workers.json (0/0 usable)",
            "  execution: repo/execute", "  continuations: repo/continuations.json",
            "  providers: 0; agents: 0", "  persistence: SQLite", "  subscriptions: none",
            "  state stores:", "  safety gates:", ""), actual);
    }

    [Fact]
    public void PrintArchitecture_PopulatedReport_PreservesCountsAndListOrder()
    {
        var report = new DistributedArchitectureReport("team blue", true,
            "tenant/state.db", "tenant/agents.json", "tenant/workers.json", "prompts", "logs",
            "tenant/continuations.json", "tenant/execute", "SQLite-backed", "CLI workers",
            ["kernel", "backlog"], ["not printed"], ["budget", "acceptance"], 2, 6, 3, 2);

        var actual = Capture(() => WorkspaceConfigurationTextView.PrintArchitecture(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "Architecture:", "  tenant: team blue scoped=True", "  state: tenant/state.db",
            "  agents: tenant/agents.json", "  workers: tenant/workers.json (2/3 usable)",
            "  execution: tenant/execute", "  continuations: tenant/continuations.json",
            "  providers: 2; agents: 6", "  persistence: SQLite-backed",
            "  subscriptions: CLI workers", "  state stores:", "    - kernel", "    - backlog",
            "  safety gates:", "    - budget", "    - acceptance", ""), actual);
    }

    private static OrchestratorWorkspace Workspace(string tenant, bool scoped, string prefix) =>
        new("root", "execute", "orchestrator", "default", false, tenant, scoped,
            prefix + "/state.db", prefix + "/agents.json", prefix + "/workers.json",
            "prompts", "logs", prefix + "/continuations.json", "transcript");

    private static string Capture(Action print)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return AsyncLocalConsoleRouter.Capture(print);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
