using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class RepositoryClaudeSettingsTests
{
    [Xunit.Fact]
    public void TrackedSettings_ContainsNoMachineSpecificAbsolutePath()
    {
        var text = File.ReadAllText(SettingsPath());
        var match = Regex.Match(text, @"[A-Za-z]:[\\/]|/c/Users", RegexOptions.IgnoreCase);

        Assert.False(match.Success, $"Machine-specific absolute path found: {match.Value}");
    }

    [Xunit.Fact]
    public void TrackedSettings_DeclaresOnlyTheTimestampPromptHook()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(SettingsPath()));
        var hooks = settings.RootElement.GetProperty("hooks");

        Assert.False(hooks.TryGetProperty("PreToolUse", out _));
        var hookEvent = Assert.Single(hooks.EnumerateObject());
        Assert.Equal("UserPromptSubmit", hookEvent.Name);
        var registration = Assert.Single(hookEvent.Value.EnumerateArray());
        var hook = Assert.Single(registration.GetProperty("hooks").EnumerateArray());
        Assert.Equal("powershell", hook.GetProperty("shell").GetString());
        Assert.Contains("Emit-Timestamp.ps1", hook.GetProperty("command").GetString(), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RepositoryHooks_RetainTimestampScriptOnly()
    {
        var hooks = Path.Combine(RepositoryRoot(), ".claude", "hooks");

        Assert.False(File.Exists(Path.Combine(hooks, "Block-CompoundShell.ps1")));
        Assert.True(File.Exists(Path.Combine(hooks, "Emit-Timestamp.ps1")));
    }

    private static string SettingsPath() => Path.Combine(RepositoryRoot(), ".claude", "settings.json");

    private static string RepositoryRoot() =>
        VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot)
            ? verifiedRoot
            : InfrastructureTestSupport.FindRepositoryRoot();
}
