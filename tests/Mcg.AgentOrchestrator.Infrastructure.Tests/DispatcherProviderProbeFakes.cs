using Mcg.AgentOrchestrator.Infrastructure;

internal static class DispatcherProviderProbeFakes
{
    public static ClaudeCliAuthState SignedInClaudeCli() => new(
        HasAnthropicApiKey: false,
        HasCliCredentialArtifact: true,
        CredentialArtifactPath: null);

    public static bool ProviderCommandsPresent(string command)
    {
        var name = Path.GetFileNameWithoutExtension(command);
        return name.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || name.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Write-Output", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Write-Host", StringComparison.OrdinalIgnoreCase)
            || name.Equals("echo", StringComparison.OrdinalIgnoreCase);
    }
}
