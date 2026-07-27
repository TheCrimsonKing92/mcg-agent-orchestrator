using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class FirewallSetupCommandTests
{
    [Xunit.Fact(DisplayName = "FirewallSetupCommand_is_retired_and_never_mutates_firewall_policy")]
    public void FirewallSetupCommandIsRetiredAndNeverMutatesFirewallPolicy()
    {
        var command = new FirewallSetupCommand();
        using var output = new StringWriter();

        var exitCode = command.Execute(output);

        Xunit.Assert.Equal(FirewallSetupCommand.SuccessExitCode, exitCode);
        Xunit.Assert.Contains("Firewall setup is retired", output.ToString());
        Xunit.Assert.Contains("Remove-TestSlotFirewallRules.ps1", output.ToString());
    }

    [Xunit.Fact(DisplayName = "FirewallSetupCommand_retirement_does_not_require_application_elevation")]
    public void FirewallSetupCommandRetirementDoesNotRequireApplicationElevation()
    {
        var command = new FirewallSetupCommand();
        using var output = new StringWriter();

        var exitCode = command.Execute(output);

        Xunit.Assert.Equal(FirewallSetupCommand.SuccessExitCode, exitCode);
        Xunit.Assert.Contains("Firewall setup is retired", output.ToString());
    }

    [Xunit.Fact(DisplayName = "Firewall_rule_removal_persists_restoration_definition_before_each_mutation")]
    public void FirewallRuleRemovalPersistsRestorationDefinitionBeforeEachMutation()
    {
        var script = File.ReadAllText(Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "scripts",
            "Remove-TestSlotFirewallRules.ps1"));
        var firstReceipt = script.IndexOf(
            "Write-RemovalReceipt -Entries $entries",
            StringComparison.Ordinal);
        var removal = script.IndexOf(
            "Remove-NetFirewallRule -Name $rule.Name",
            StringComparison.Ordinal);

        Xunit.Assert.True(firstReceipt >= 0);
        Xunit.Assert.True(removal > firstReceipt);
        Xunit.Assert.Contains("$entry.removalState = \"removing\"", script, StringComparison.Ordinal);
        Xunit.Assert.Contains("[System.IO.File]::Move($temporaryPath, $ReceiptPath, $true)", script, StringComparison.Ordinal);
    }
}
