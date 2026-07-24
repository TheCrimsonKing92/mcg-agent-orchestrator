using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class FirewallSetupCommandTests
{
    [Xunit.Fact(DisplayName = "FirewallSetupCommand_computes_stable_slot_testhost_rules_idempotently")]
    public void FirewallSetupCommandComputesStableSlotTesthostRulesIdempotently()
    {
        var previousRoot = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        var root = Path.Combine(Path.GetTempPath(), "mcg-firewall-test-root");
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, root);
        try
        {
            var writer = new RecordingFirewallRuleWriter();
            var command = new FirewallSetupCommand(writer, () => true);

            using var firstOutput = new StringWriter();
            var firstExit = command.Execute(firstOutput);
            using var secondOutput = new StringWriter();
            var secondExit = command.Execute(secondOutput);

            Xunit.Assert.Equal(FirewallSetupCommand.SuccessExitCode, firstExit);
            Xunit.Assert.Equal(FirewallSetupCommand.SuccessExitCode, secondExit);
            Xunit.Assert.Contains("created 24 rule(s), already present 0, total 24", firstOutput.ToString());
            Xunit.Assert.Contains("created 0 rule(s), already present 24, total 24", secondOutput.ToString());
            Xunit.Assert.Equal(24, writer.Rules.Count);

            var expected = new List<FirewallRuleSpec>();
            for (var slot = 0; slot < 4; slot++)
            {
                expected.Add(Expected(slot, "Core", "Mcg.AgentOrchestrator.Core.Tests", "Debug", root));
                expected.Add(Expected(slot, "Core", "Mcg.AgentOrchestrator.Core.Tests", "Release", root));
                expected.Add(Expected(slot, "Infrastructure", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Debug", root));
                expected.Add(Expected(slot, "Infrastructure", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Release", root));
            }

            for (var slot = 0; slot < 4; slot++)
            {
                foreach (var projectName in new[]
                {
                    "Mcg.AgentOrchestrator.Core.Tests",
                    "Mcg.AgentOrchestrator.Infrastructure.Tests"
                })
                {
                    expected.Add(new FirewallRuleSpec(
                        $"MCG-testhost-slot{slot}-{projectName}-MTP-Debug",
                        Path.Combine(
                            root,
                            "slots",
                            $"slot-{slot}",
                            "artifacts",
                            "bin",
                            projectName,
                            "debug",
                            $"{projectName}.exe")));
                }
            }

            Xunit.Assert.Equal(expected, writer.Rules);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, previousRoot);
        }
    }

    [Xunit.Fact(DisplayName = "FirewallSetupCommand_non_admin_prints_elevated_command")]
    public void FirewallSetupCommandNonAdminPrintsElevatedCommand()
    {
        var writer = new RecordingFirewallRuleWriter();
        var command = new FirewallSetupCommand(writer, () => false);
        using var output = new StringWriter();

        var exitCode = command.Execute(output);

        Xunit.Assert.Equal(FirewallSetupCommand.ElevationRequiredExitCode, exitCode);
        Xunit.Assert.Empty(writer.Rules);
        Xunit.Assert.Contains("Windows Firewall setup requires administrator elevation.", output.ToString());
        Xunit.Assert.Contains(FirewallSetupCommand.ElevatedCommand, output.ToString());
    }

    private static FirewallRuleSpec Expected(
        int slot,
        string ruleProject,
        string artifactProject,
        string configuration,
        string root)
    {
        return new FirewallRuleSpec(
            $"MCG-testhost-slot{slot}-{ruleProject}-{configuration}",
            Path.Combine(
                root,
                "slots",
                $"slot-{slot}",
                "artifacts",
                "bin",
                artifactProject,
                $"{configuration.ToLowerInvariant()}_net10.0",
                "testhost.exe"));
    }

    private sealed class RecordingFirewallRuleWriter : IFirewallRuleWriter
    {
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

        public List<FirewallRuleSpec> Rules { get; } = [];

        public bool CreateInboundAllowRule(FirewallRuleSpec rule)
        {
            if (!_names.Add(rule.Name))
            {
                return false;
            }

            Rules.Add(rule);
            return true;
        }
    }
}
