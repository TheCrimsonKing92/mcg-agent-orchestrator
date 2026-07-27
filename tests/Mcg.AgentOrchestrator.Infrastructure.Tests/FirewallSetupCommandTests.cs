using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class FirewallSetupCommandTests
{
    [Xunit.Fact(DisplayName = "FirewallSetupCommand_is_retired_and_never_mutates_firewall_policy")]
    public void FirewallSetupCommandIsRetiredAndNeverMutatesFirewallPolicy()
    {
        var writer = new RecordingFirewallRuleWriter();
        var command = new FirewallSetupCommand(writer, () => true);
        using var output = new StringWriter();

        var exitCode = command.Execute(output);

        Xunit.Assert.Equal(FirewallSetupCommand.SuccessExitCode, exitCode);
        Xunit.Assert.Empty(writer.Rules);
        Xunit.Assert.Contains("Firewall setup is retired", output.ToString());
        Xunit.Assert.Contains("Remove-TestSlotFirewallRules.ps1", output.ToString());
    }

    [Xunit.Fact(DisplayName = "FirewallSetupCommand_retirement_does_not_require_application_elevation")]
    public void FirewallSetupCommandRetirementDoesNotRequireApplicationElevation()
    {
        var writer = new RecordingFirewallRuleWriter();
        var command = new FirewallSetupCommand(writer, () => false);
        using var output = new StringWriter();

        var exitCode = command.Execute(output);

        Xunit.Assert.Equal(FirewallSetupCommand.SuccessExitCode, exitCode);
        Xunit.Assert.Empty(writer.Rules);
        Xunit.Assert.Contains("Firewall setup is retired", output.ToString());
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
