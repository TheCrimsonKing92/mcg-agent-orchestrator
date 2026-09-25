namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal static bool ResolveSelfRelaunchEnabled(string? configuredValue, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return DefaultSelfRelaunchEnabled;
        }

        var value = configuredValue.Trim();
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            return false;
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            return true;
        }

        warn?.Invoke($"SELF_RELAUNCH_SETTING_MALFORMED value={value.Replace('\r', ' ').Replace('\n', ' ')} resolved=enabled");
        return true;
    }
}
