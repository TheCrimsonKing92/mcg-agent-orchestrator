using Mcg.AgentOrchestrator.App.Cli;
using Xunit;

// Parallel-safe: these facts inspect only argv and the read-only verb set.
public sealed class CliCommandCapabilitiesTestsQueryRouteConvention
{
    private static readonly IReadOnlyDictionary<string, string[][]> ServedForms =
        new Dictionary<string, string[][]>(StringComparer.OrdinalIgnoreCase)
        {
            ["tasks"] = [["tasks"]],
            ["task"] = [["task", "abc10000"]],
            ["status"] = [["status", "abc10000"], ["status"],
                ["status", "abc10000", "--tasks-only"], ["status", "--tasks-only", "abc10000"], ["status", "--tasks-only"]],
            ["readiness"] = [["readiness", "abc10000"], ["readiness"]],
            ["goals"] = [["goals"]],
            ["failure-clusters"] = [["failure-clusters"]],
            ["lane-reuse-shadow"] = [["lane-reuse-shadow"]],
            ["remote-executors"] = [["remote-executors"]],
            ["round-value"] = [["round-value"]],
            ["architecture"] = [["architecture"]],
            ["config"] = [["config", "agents"], ["config", "profiles"], ["config", "policy"]],
            ["model-outcomes"] = [["model-outcomes"]],
            ["backlog-list"] = [["backlog-list"]],
            ["backlog-show"] = [["backlog-show", "abc10000"]],
            ["backlog-similar"] = [["backlog-similar", "query"]],
            ["goal-events"] = [["goal-events", "abc10000"]],
            ["timeline"] = [["timeline", "abc10000"]],
            ["next"] = [["next", "--full", "abc10000"], ["next", "abc10000"], ["next"],
                ["next", "abc10000", "--autonomy", "observe"], ["next", "abc10000", "--autonomy-policy", "observe"],
                ["next", "--autonomy", "observe"], ["next", "--autonomy-policy", "observe"]]
        };

    private static readonly IReadOnlyDictionary<string, string> Exemptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["monitor-goal"] = "Polling loop deferred until its reload and clock seam is decided.",
            ["owner-digest"] = "Served at startup before the persistent runner.",
            ["context-usage"] = "Served at startup before the persistent runner."
        };

    private static readonly (string[] Args, string Reason)[] WriterPathForms =
    [
        (["status", "abc10000", "--tasks-only", "--tasks-only"], "Repeated tasks-only flags keep the writer path."),
        (["status", "abc10000", "extra", "--tasks-only"], "Extra positional arguments keep the writer path."),
    ];

    [Fact]
    public void QueryOnlyVerbs_TableMembership_FormsAnExactPartition()
    {
        var failures = new List<string>();
        foreach (var verb in CliCommandCapabilities.QueryOnlyVerbs)
        {
            if (ServedForms.ContainsKey(verb) == Exemptions.ContainsKey(verb))
                failures.Add($"{verb}: must appear in exactly one of ServedForms and Exemptions.");
        }

        foreach (var verb in ServedForms.Keys.Concat(Exemptions.Keys))
        {
            if (!CliCommandCapabilities.QueryOnlyVerbs.Contains(verb))
                failures.Add($"{verb}: table key is outside QueryOnlyVerbs.");
        }

        AssertNoFailures(failures);
    }

    [Fact]
    public void ServedForms_ListedArguments_SelectReadOnlyRouteAndQueryComposition()
    {
        var failures = new List<string>();
        foreach (var (verb, forms) in ServedForms)
        {
            if (forms.Length == 0)
                failures.Add($"{verb}: needs at least one pure-read form.");

            foreach (var args in forms)
            {
                var form = string.Join(" ", args);
                if (args.Length == 0 || !verb.Equals(args[0], StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{verb}: form '{form}' must begin with its table key.");
                if (!CliReadOnlyCommandRunner.IsReadOnlyCommand(args))
                    failures.Add($"{form}: read-only route declined ServedForms entry.");
                if (CliCommandCapabilities.Classify(args) != CliCommandCapability.QueryOnly)
                    failures.Add($"{form}: ServedForms entry must classify QueryOnly.");
            }
        }

        AssertNoFailures(failures);
    }

    [Fact]
    public void Exemptions_BareForms_PinFiveReasonedQueryOnlyExceptions()
    {
        string[] expected = ["monitor-goal", "owner-digest", "context-usage"];
        var failures = new List<string>();
        foreach (var verb in expected)
        {
            if (!Exemptions.ContainsKey(verb))
                failures.Add($"{verb}: required exemption is missing.");
        }

        foreach (var (verb, reason) in Exemptions)
        {
            if (!expected.Contains(verb, StringComparer.OrdinalIgnoreCase))
                failures.Add($"{verb}: unexpected exemption.");
            if (string.IsNullOrWhiteSpace(reason))
                failures.Add($"{verb}: exemption needs a reason.");
            if (CliReadOnlyCommandRunner.IsReadOnlyCommand([verb]))
                failures.Add($"{verb}: bare exempt form unexpectedly selects the read-only route.");
            if (CliCommandCapabilities.Classify([verb]) != CliCommandCapability.QueryOnly)
                failures.Add($"{verb}: bare exempt form must retain QueryOnly composition.");
        }

        AssertNoFailures(failures);
    }

    [Fact]
    public void StartupQueries_BareForms_MatchTheirStartupPredicates()
    {
        Assert.True(CliOwnerDigestCommand.IsCommand(["owner-digest"]));
        Assert.True(CliContextUsageCommand.IsCommand(["context-usage"]));
    }

    [Fact]
    public void WriterPathForms_ListedArguments_RetainQueryOnlyComposition()
    {
        var failures = new List<string>();
        foreach (var (args, reason) in WriterPathForms)
        {
            var form = string.Join(" ", args);
            if (args.Length == 0 || !CliCommandCapabilities.QueryOnlyVerbs.Contains(args[0]) ||
                !ServedForms.ContainsKey(args[0]))
                failures.Add($"{form}: writer-path form must name a served QueryOnly verb.");
            if (string.IsNullOrWhiteSpace(reason))
                failures.Add($"{form}: writer-path form needs a reason.");
            if (CliReadOnlyCommandRunner.IsReadOnlyCommand(args))
                failures.Add($"{form}: writer-path form unexpectedly selects the read-only route.");
            if (CliCommandCapabilities.Classify(args) != CliCommandCapability.QueryOnly)
                failures.Add($"{form}: writer-path form must retain QueryOnly composition.");
        }

        string[][] required = [["status", "abc10000", "--tasks-only", "--tasks-only"]];
        foreach (var args in required)
        {
            if (!WriterPathForms.Any(form => form.Args.SequenceEqual(args, StringComparer.OrdinalIgnoreCase)))
                failures.Add($"{string.Join(" ", args)}: required writer-path form is missing.");
        }

        AssertNoFailures(failures);
    }

    [Fact]
    public void BacklogDepends_WriteForms_SelectExecutionComposition()
    {
        string[][] forms =
        [
            ["backlog-depends", "abc", "--on", "def"],
            ["backlog-depends", "abc", "--remove", "def"],
            ["backlog-depends", "abc", "--clear"]
        ];
        var failures = new List<string>();
        foreach (var args in forms)
        {
            if (CliCommandCapabilities.Classify(args) != CliCommandCapability.Execution)
                failures.Add($"{string.Join(" ", args)}: backlog dependency writes must classify Execution.");
        }

        AssertNoFailures(failures);
    }

    [Fact]
    public void BacklogReads_ExistingForms_RetainQueryOnlyComposition()
    {
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(["backlog-list"]));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(["backlog-similar", "query"]));
    }

    private static void AssertNoFailures(List<string> failures) =>
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
}
