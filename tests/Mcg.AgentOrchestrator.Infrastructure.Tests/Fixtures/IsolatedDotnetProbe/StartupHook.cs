using System.Text.Json;

// Observe the real trusted dotnet host without substituting a generated executable or shell script.
internal static class StartupHook
{
    public static void Initialize()
    {
        var arguments = Environment.GetCommandLineArgs();
        var encodingReceipt = Environment.GetEnvironmentVariable("MCG_CLI_OUTPUT_ENCODING_RECEIPT");
        if (!string.IsNullOrWhiteSpace(encodingReceipt) && arguments.Length > 1 &&
            Path.GetFileName(arguments[0]) == "Mcg.AgentOrchestrator.App.dll" && arguments[1] == "goal")
        {
            // This hook belongs only to the explicitly instrumented CLI child, not
            // its refinement/worker descendants or the calling test host.
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
            Environment.SetEnvironmentVariable("MCG_CLI_OUTPUT_ENCODING_RECEIPT", null);
            Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(encodingReceipt, Console.OutputEncoding.CodePage.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var receipt = Environment.GetEnvironmentVariable("MCG_WORKSPACE_BINDING_HOST_RECEIPT");
        if (string.IsNullOrWhiteSpace(receipt) || !arguments.Contains("__workspace-binding-check", StringComparer.Ordinal))
            return;

        File.WriteAllText(receipt, JsonSerializer.Serialize(new { host = Environment.ProcessPath, arguments }));
    }
}
