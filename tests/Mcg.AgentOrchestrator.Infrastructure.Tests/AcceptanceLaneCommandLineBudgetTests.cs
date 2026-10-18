using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: reads the shipped manifest; writes response files only under owned temporary roots.
public sealed class AcceptanceLaneCommandLineBudgetTests
{
    private const int CmdLimit = 8191;
    private const int CommandLineBudget = CmdLimit - 256;

    [Fact]
    public void ShippedLanes_WindowsCommandComposition_LeavesCaptureAndPathHeadroom()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        // Use the verifier's collection ownership resolution, since it can add filter operands.
        var lanes = AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, root);
        Assert.NotEmpty(lanes);
        var artifacts = Path.Combine(Path.GetTempPath(), $"lane-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        try
        {
            foreach (var lane in lanes)
                AssertWithinBudget(lane.Name, CommandLength(lane, settings, root, artifacts, out _));
        }
        finally { Directory.Delete(artifacts, recursive: true); }
    }

    [Fact]
    public void OversizedSyntheticLane_WindowsCommandComposition_SpillsWithinBudget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var exclusions = Enumerable.Range(0, 600).Select(index => $"NoSuchLaneClass{index:D4}").ToArray();
        var filter = string.Join('&', exclusions.Select(name => "FullyQualifiedName!~" + name));
        var synthetic = settings.InfrastructureTestLanes[0] with
        {
            Name = "Oversized synthetic lane",
            Filter = filter + "&Category!=HostIntegration"
        };
        var artifacts = Path.Combine(Path.GetTempPath(), $"lane-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        try
        {
            Assert.True(filter.Length >= 20000);
            var length = CommandLength(synthetic, settings, root, artifacts, out var spill);
            AssertWithinBudget(synthetic.Name, length);
            Assert.NotNull(spill.ResponseFilePath);
            Assert.Equal(spill.ResponseFileText, File.ReadAllText(spill.ResponseFilePath));
            var entries = spill.ResponseFileText!.Split('\n');
            Assert.Equal(exclusions.SelectMany(name => new[] { "--filter-not-class", $"*{name}*" })
                .Concat(["--filter-not-trait", "Category=HostIntegration"]), entries);
        }
        finally { Directory.Delete(artifacts, recursive: true); }
    }

    private static void AssertWithinBudget(string laneName, int length) =>
        Assert.True(length <= CommandLineBudget, BudgetFailure(laneName, length));

    private static string BudgetFailure(string laneName, int length) =>
        $"Lane '{laneName}' command line is {length} characters; limit is {CommandLineBudget}.";

    private static int CommandLength(AcceptanceTestLane lane, AcceptanceGateEngineSettings settings, string root,
        string artifacts, out AcceptanceMtpFilterSpill spill)
    {
        var template = Assert.Single(settings.DeclaredDotnetTestChecks,
            check => check.Name == "infrastructure tests");
        // Same shard inputs as GoalAcceptanceVerifier.BuildInfrastructureShardCheck.
        var check = new AcceptanceManifestCheck
        {
            Name = $"{template.Name}: {lane.Name}",
            Type = template.Type,
            Project = template.Project,
            Runner = template.Runner,
            Arguments = [.. template.Arguments, "--filter", lane.Filter],
            IsResolvedInfrastructureLane = true
        };
        // The 256-character reserve covers longer runtime paths and attempt identifiers.
        var environment = new DotnetBuildEnvironment("3c8fc392", artifacts, artifacts,
            Path.Combine(artifacts, "lease.lock"), [], "owner");
        var prefix = Path.Combine(artifacts, "TestResults", "3c8fc392-0-20261018173057807");
        var telemetry = GoalAcceptanceVerifier.ResolveTestTelemetryCore(check, environment, prefix, 0);
        var arguments = AcceptanceCheckCommandBuilder.BuildMtpTestArguments(check, settings, environment,
            telemetry, excludeHostIntegration: true,
            (_, filter) => AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter));
        spill = AcceptanceMtpFilterResponseFile.Plan(arguments);
        // Pipe GUIDs have the same width as the production invoker's per-run capture names.
        const string stdout = @"\\.\pipe\mcg-acc-00000000000000000000000000000000-out";
        const string stderr = @"\\.\pipe\mcg-acc-00000000000000000000000000000000-err";
        try
        {
            return GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(arguments, root, stdout, stderr,
                forceUtf8ConsoleOutput: true).Arguments.Length;
        }
        catch (CommandLineTooLongException error)
        {
            // Feed the production rejection through the same budget assertion and lane diagnostic.
            return error.Length;
        }
    }
}
