using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record FirewallRuleSpec(string Name, string ProgramPath);

public interface IFirewallRuleWriter
{
    bool CreateInboundAllowRule(FirewallRuleSpec rule);
}

public sealed class FirewallSetupCommand
{
    public const int SuccessExitCode = 0;
    public const int ErrorExitCode = 1;
    public const int ElevationRequiredExitCode = 2;
    public const string ElevatedCommand = "Start-Process -Verb RunAs pwsh -ArgumentList '-Command mcg firewall-setup'";

    private readonly IFirewallRuleWriter _ruleWriter;
    private readonly Func<bool> _isAdministrator;

    public FirewallSetupCommand(IFirewallRuleWriter ruleWriter, Func<bool>? isAdministrator = null)
    {
        _ruleWriter = ruleWriter;
        _isAdministrator = isAdministrator ?? IsAdministrator;
    }

    public int Execute(TextWriter output)
    {
        if (!_isAdministrator())
        {
            output.WriteLine("Windows Firewall setup requires administrator elevation.");
            output.WriteLine(ElevatedCommand);
            output.WriteLine("Run that command in PowerShell once to pre-authorize the orchestrator's stable testhost paths.");
            return ElevationRequiredExitCode;
        }

        var created = 0;
        var existing = 0;
        foreach (var path in DotnetBuildEnvironmentManager.StableSlotTestExecutableFirewallPaths())
        {
            var ruleName = $"MCG-testhost-slot{path.SlotIndex}-{path.Project}-{path.Configuration}";
            if (_ruleWriter.CreateInboundAllowRule(new FirewallRuleSpec(ruleName, path.Path)))
            {
                created++;
            }
            else
            {
                existing++;
            }
        }

        output.WriteLine($"Firewall setup: created {created} rule(s), already present {existing}, total {created + existing}.");
        return SuccessExitCode;
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public sealed class WindowsFirewallRuleWriter : IFirewallRuleWriter
{
    public bool CreateInboundAllowRule(FirewallRuleSpec rule)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Firewall setup is only supported on Windows.");
        }

        var policy = CreateComObject("HNetCfg.FwPolicy2");
        if (TryGetExistingRule(policy, rule.Name) is not null)
        {
            return false;
        }

        var firewallRule = CreateComObject("HNetCfg.FWRule");
        firewallRule.Name = rule.Name;
        firewallRule.Description = "MCG Agent Orchestrator stable-slot testhost.exe allowance.";
        firewallRule.ApplicationName = rule.ProgramPath;
        firewallRule.Direction = 1; // NET_FW_RULE_DIR_IN
        firewallRule.Action = 1; // NET_FW_ACTION_ALLOW
        firewallRule.Protocol = 256; // NET_FW_IP_PROTOCOL_ANY
        firewallRule.RemoteAddresses = "*";
        firewallRule.Enabled = true;
        policy.Rules.Add(firewallRule);
        return true;
    }

    private static dynamic CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ??
            throw new InvalidOperationException($"Windows Firewall COM object '{progId}' is not available.");
        return Activator.CreateInstance(type) ??
            throw new InvalidOperationException($"Windows Firewall COM object '{progId}' could not be created.");
    }

    private static object? TryGetExistingRule(dynamic policy, string name)
    {
        try
        {
            return policy.Rules.Item(name);
        }
        catch (COMException)
        {
            return null;
        }
    }
}
