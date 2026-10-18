using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: reads the shipped manifest and composes arguments without starting processes or writing artifacts.
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
        foreach (var lane in lanes)
            AssertWithinBudget(lane.Name, CommandLength(lane, settings, root));
    }

    [Fact]
    public void OversizedSyntheticLane_WindowsCommandComposition_ReportsLaneLengthAndLimit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var synthetic = settings.InfrastructureTestLanes[0] with
        {
            Name = "Oversized synthetic lane",
            Filter = $"FullyQualifiedName~{new string('X', CmdLimit)}&Category!=HostIntegration"
        };
        var length = CommandLength(synthetic, settings, root);
        Assert.True(length > CmdLimit);
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertWithinBudget(synthetic.Name, length));
        Assert.StartsWith(BudgetFailure(synthetic.Name, length), error.Message, StringComparison.Ordinal);
    }

    private static void AssertWithinBudget(string laneName, int length) =>
        Assert.True(length <= CommandLineBudget, BudgetFailure(laneName, length));

    private static string BudgetFailure(string laneName, int length) =>
        $"Lane '{laneName}' command line is {length} characters; limit is {CommandLineBudget}.";

    private static int CommandLength(AcceptanceTestLane lane, AcceptanceGateEngineSettings settings, string root)
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
        // Fixed representative Windows slot paths keep the verdict independent of the host profile.
        // The 256-character reserve covers longer runtime paths and attempt identifiers.
        const string artifacts = @"C:\Users\acceptance\AppData\LocalLow\mcg-dotnet-isolated\slots\slot-0\artifacts";
        var environment = new DotnetBuildEnvironment("3c8fc392", @"C:\acceptance", artifacts,
            @"C:\acceptance\lease.lock", [], "owner");
        var prefix = Path.Combine(artifacts, "TestResults", "3c8fc392-0-20261018173057807");
        var telemetry = GoalAcceptanceVerifier.ResolveTestTelemetryCore(check, environment, prefix, 0);
        var arguments = AcceptanceCheckCommandBuilder.BuildMtpTestArguments(check, settings, environment,
            telemetry, excludeHostIntegration: true,
            (_, filter) => AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter));
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
