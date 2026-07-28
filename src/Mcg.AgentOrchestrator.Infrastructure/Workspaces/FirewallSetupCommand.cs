namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class FirewallSetupCommand
{
    public const int SuccessExitCode = 0;

    public int Execute(TextWriter output)
    {
        output.WriteLine("Firewall setup is retired: MTP tests execute in-process from owner-scoped paths.");
        output.WriteLine("After the documented soak period, run scripts/Remove-TestSlotFirewallRules.ps1 from an elevated PowerShell session.");
        return SuccessExitCode;
    }
}
