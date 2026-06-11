namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OrchestratorTenantSelection(string TenantName, IReadOnlyList<string> CommandArgs)
{
    public const string TenantEnvironmentVariable = "MCG_ORCHESTRATOR_TENANT";

    public static OrchestratorTenantSelection FromArgs(
        IReadOnlyList<string> args,
        string? environmentTenant)
    {
        var tenantName = environmentTenant;
        var commandArgs = new List<string>(args.Count);
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg.Equals("--tenant", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("Usage: --tenant <tenant-name> must include a tenant name.");
                }

                tenantName = args[index + 1];
                index++;
                continue;
            }

            if (arg.StartsWith("--tenant=", StringComparison.OrdinalIgnoreCase))
            {
                tenantName = arg["--tenant=".Length..];
                continue;
            }

            commandArgs.Add(arg);
        }

        return new OrchestratorTenantSelection(NormalizeTenantName(tenantName), commandArgs);
    }

    public static string NormalizeTenantName(string? tenantName)
    {
        if (string.IsNullOrWhiteSpace(tenantName))
        {
            return OrchestratorWorkspace.DefaultTenantName;
        }

        var trimmed = tenantName.Trim();
        if (trimmed.Length > 64)
        {
            throw new ArgumentException("Tenant name must be 64 characters or fewer.");
        }

        if (trimmed is "." or "..")
        {
            throw new ArgumentException("Tenant name cannot be a relative path segment.");
        }

        foreach (var ch in trimmed)
        {
            var allowed = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_';
            if (!allowed)
            {
                throw new ArgumentException("Tenant name may contain only ASCII letters, digits, '-' and '_'.");
            }
        }

        return trimmed;
    }
}
