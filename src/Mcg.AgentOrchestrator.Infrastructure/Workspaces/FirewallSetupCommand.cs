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
    private readonly string _manifestRoot;

    public FirewallSetupCommand(
        IFirewallRuleWriter ruleWriter,
        Func<bool>? isAdministrator = null,
        string? manifestRoot = null)
    {
        _ruleWriter = ruleWriter;
        _isAdministrator = isAdministrator ?? IsAdministrator;
        _manifestRoot = manifestRoot ?? ResolveDefaultManifestRoot();
    }

    public int Execute(TextWriter output)
    {
        output.WriteLine("Firewall setup is retired: MTP tests execute in-process from owner-scoped paths.");
        output.WriteLine("After the documented soak period, run scripts/Remove-TestSlotFirewallRules.ps1 from an elevated PowerShell session.");
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

    private static string ResolveDefaultManifestRoot()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT"),
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };
        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = new DirectoryInfo(Path.GetFullPath(candidate!));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "config", "acceptance-manifest.json")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
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
