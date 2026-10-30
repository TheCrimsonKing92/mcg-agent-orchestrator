using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and injected command resolution.
// Expected strings derive from ConsoleViews.Configuration.cs at base 618b883c4.
public sealed class WorkerConfigurationTextViewCharacterizationTests
{
    [Fact]
    public void PrintModelFunctions_Empty_PreservesExactText()
    {
        Assert.Equal("Model functions: (none configured)" + Environment.NewLine,
            Capture(() => WorkerConfigurationTextView.PrintModelFunctions(ModelFunctionCatalog.Empty)));
    }

    [Fact]
    public void PrintModelFunctions_Bindings_PreservesOrderAndOptionalNames()
    {
        var catalog = new ModelFunctionCatalog([
            new("judge", ModelLane.Capable, Model("OpenAI", "judge-model"), "Review"),
            new("summary", ModelLane.Local, Model("Ollama", "local-model")),
            new("sample", ModelLane.CheapApi, Model("Anthropic", "small-model"), " ")]);

        var actual = Capture(() => WorkerConfigurationTextView.PrintModelFunctions(catalog));

        Assert.Equal(string.Join(Environment.NewLine,
            "Model functions (orchestrator-internal model uses, not workers):",
            "  judge [Capable]: OpenAI/judge-model (Review)",
            "  summary [Local]: Ollama/local-model",
            "  sample [CheapApi]: Anthropic/small-model", ""), actual);
    }

    [Fact]
    public void PrintWorkerProfiles_Empty_PreservesExactText()
    {
        Assert.Equal("Worker profiles:" + Environment.NewLine,
            Capture(() => WorkerConfigurationTextView.PrintWorkerProfiles(new([]))));
    }

    [Fact]
    public void PrintWorkerProfiles_Unsorted_PreservesCaseInsensitiveOrdering()
    {
        var catalog = new WorkerProfileCatalog([
            new("beta-worker", "beta --model {subscriptionModelName}"),
            new("Alpha-worker", "alpha --prompt {promptPath}")]);

        var actual = Capture(() => WorkerConfigurationTextView.PrintWorkerProfiles(catalog));

        Assert.Equal(string.Join(Environment.NewLine,
            "Worker profiles:",
            "  Alpha-worker: alpha --prompt {promptPath}",
            "  beta-worker: beta --model {subscriptionModelName}", ""), actual);
    }

    [Fact]
    public void PrintWorkerProfileChecks_Empty_PreservesExactText()
    {
        Assert.Equal("Worker profile checks:" + Environment.NewLine,
            Capture(() => WorkerConfigurationTextView.PrintWorkerProfileChecks(
                new([]), new([]), null, _ => false)));
    }

    [Fact]
    public void PrintWorkerProfileChecks_Resolvable_PreservesCatalogOrder()
    {
        var profiles = new WorkerProfileCatalog([
            new("beta-worker", "beta --model {subscriptionModelName}"),
            new("alpha-worker", "alpha --model {subscriptionModelName}")]);

        var actual = Capture(() => WorkerConfigurationTextView.PrintWorkerProfileChecks(
            new([]), profiles, null, _ => true));

        Assert.Equal(string.Join(Environment.NewLine,
            "Worker profile checks:",
            "  beta-worker: executable=beta resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)",
            "  alpha-worker: executable=alpha resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)",
            ""), actual);
    }

    [Fact]
    public void PrintWorkerProfileChecks_Filtered_PreservesExactText()
    {
        var profiles = new WorkerProfileCatalog([
            new("broken-worker", "missing"),
            new("alpha-worker", "alpha --model {subscriptionModelName}")]);

        var actual = Capture(() => WorkerConfigurationTextView.PrintWorkerProfileChecks(
            new([]), profiles, "ALPHA-WORKER", executable => executable == "alpha"));

        Assert.Equal(string.Join(Environment.NewLine,
            "Worker profile checks:",
            "  alpha-worker: executable=alpha resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)",
            ""), actual);
    }

    [Fact]
    public void PrintWorkerProfileChecks_Unresolvable_WritesBeforeThrowing()
    {
        var profiles = new WorkerProfileCatalog([new("broken-worker", "missing")]);

        var actual = Capture(() =>
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                WorkerConfigurationTextView.PrintWorkerProfileChecks(new([]), profiles, null, _ => false));
            Assert.Equal("One or more worker profiles are not resolvable.", error.Message);
        });

        Assert.Equal(string.Join(Environment.NewLine,
            "Worker profile checks:",
            "  broken-worker: executable=missing resolvable=False patchCapable=True (Command was not found on PATH or as a PowerShell command.)",
            ""), actual);
    }

    [Fact]
    public void PrintWorkerProfileChecks_MissingRoute_WritesBeforeThrowing()
    {
        var agents = new AgentCatalog([new(new("planner"), "Planner", AgentRole.Planner,
            Model("OpenAI", "worker-model"), ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new("absent-worker"))]);

        var actual = Capture(() =>
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                WorkerConfigurationTextView.PrintWorkerProfileChecks(agents, new([]), null, _ => true));
            Assert.Equal("One or more active subscription routes are not usable.", error.Message);
        });

        Assert.Equal(string.Join(Environment.NewLine,
            "Worker profile checks:",
            "  route Planner: profile=absent-worker (subscription profile is not configured)", ""), actual);
    }

    [Fact]
    public void PrintWorkerProfileChecks_UnknownName_ThrowsWithoutOutput()
    {
        var actual = Capture(() =>
        {
            var error = Assert.Throws<KeyNotFoundException>(() =>
                WorkerConfigurationTextView.PrintWorkerProfileChecks(new([]), new([]), "missing", _ => true));
            Assert.Equal("Worker profile 'missing' was not found.", error.Message);
        });

        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void PrintAgents_Empty_PreservesExactText()
    {
        Assert.Equal("Agents:" + Environment.NewLine,
            Capture(() => WorkerConfigurationTextView.PrintAgents([])));
    }

    [Fact]
    public void PrintAgents_MixedRoster_PreservesRoutesDefaultsAndRoleOrdering()
    {
        AgentDefinition[] agents =
        [
            new(new("dev-busy"), "Busy developer", AgentRole.Developer, Model("OpenAI", "dev-model"),
                Status: AgentStatus.Busy, IsProviderRoutingConstrained: false),
            new(new("planner"), "Plan worker", AgentRole.Planner, Model("Anthropic", "plan-model"),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new("plan-cli"), IsProviderRoutingConstrained: true),
            new(new("dev-ready"), "Ready developer", AgentRole.Developer,
                Model("OpenAI", "dev-model") with { ReasoningEffort = "high" },
                ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
                Subscription: new("dev-cli", "selected-model", "medium")),
            new(new("dev-alternate"), "Alternate developer", AgentRole.Developer,
                Model("OpenAI", "alternate-model"))
        ];

        var actual = Capture(() => WorkerConfigurationTextView.PrintAgents(agents));

        Assert.Equal(string.Join(Environment.NewLine,
            "Agents:",
            "  Planner: Plan worker id=planner route=primary provider-routing=constrained execution=SubscriptionOnly api=Anthropic/plan-model reasoning=default subscription=plan-cli model=default reasoning=default status=Available",
            "  Developer: Busy developer id=dev-busy route=alternate provider-routing=automatic execution=ApiOnly api=OpenAI/dev-model reasoning=default subscription=none status=Busy",
            "  Developer: Ready developer id=dev-ready route=primary provider-routing=unspecified execution=PreferSubscription api=OpenAI/dev-model reasoning=high subscription=dev-cli model=selected-model reasoning=medium status=Available",
            "  Developer: Alternate developer id=dev-alternate route=alternate provider-routing=unspecified execution=ApiOnly api=OpenAI/alternate-model reasoning=default subscription=none status=Available",
            ""), actual);
    }

    private static ModelProfile Model(string provider, string name) =>
        new(provider, name, ModelCapability.Text, SubscriptionMode.ApiKey);

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
