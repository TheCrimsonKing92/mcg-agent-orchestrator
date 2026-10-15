using System.Text.Json;

// Observe the real trusted dotnet host without substituting a generated executable or shell script.
internal static class StartupHook
{
    public static void Initialize()
    {
        var receipt = Environment.GetEnvironmentVariable("MCG_WORKSPACE_BINDING_HOST_RECEIPT");
        var arguments = Environment.GetCommandLineArgs();
        if (string.IsNullOrWhiteSpace(receipt) || !arguments.Contains("__workspace-binding-check", StringComparer.Ordinal))
            return;

        File.WriteAllText(receipt, JsonSerializer.Serialize(new { host = Environment.ProcessPath, arguments }));
    }
}
