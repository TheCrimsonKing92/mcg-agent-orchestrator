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
        var scriptPath = Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "scripts",
            "Remove-TestSlotFirewallRules.ps1");
        AssertPowerShellParses(scriptPath);
        var script = File.ReadAllText(scriptPath);
        var firstReceipt = script.IndexOf(
            "Write-RemovalReceipt -Entries $entries",
            StringComparison.Ordinal);
        var removal = script.IndexOf(
            "Remove-NetFirewallRule -Name $rule.Name",
            StringComparison.Ordinal);

        Xunit.Assert.True(firstReceipt >= 0);
        Xunit.Assert.True(removal > firstReceipt);
        Xunit.Assert.Contains("#Requires -Version 7.0", script, StringComparison.Ordinal);
        Xunit.Assert.Contains("$entry.removalState = \"removing\"", script, StringComparison.Ordinal);
        Xunit.Assert.Contains("[System.IO.File]::Move($temporaryPath, $ReceiptPath, $true)", script, StringComparison.Ordinal);
    }

    private static void AssertPowerShellParses(string scriptPath)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-CommandWithArgs");
        process.StartInfo.ArgumentList.Add(
            "$tokens = $null; $errors = $null; " +
            "[System.Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$tokens, [ref]$errors) > $null; " +
            "if ($errors.Count -gt 0) { $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"PowerShell parser did not exit for '{scriptPath}'.");
        }

        Xunit.Assert.True(
            process.ExitCode == 0,
            $"PowerShell parser rejected '{scriptPath}': {stderr.GetAwaiter().GetResult()}{stdout.GetAwaiter().GetResult()}");
    }
}
