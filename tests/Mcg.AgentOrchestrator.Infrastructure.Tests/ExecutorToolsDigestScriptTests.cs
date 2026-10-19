using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Real PowerShell contract: child environment and all output paths belong to the fixture.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ExecutorToolsDigestScriptTests : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public async Task Script_TopLevelScripts_WritesMatchingDigestAndOrderedArray(int count)
    {
        var tools = Path.Combine(_root, "tools");
        Directory.CreateDirectory(Path.Combine(tools, "bin"));
        File.WriteAllBytes(Path.Combine(tools, "Z.PS1"), [0xEF, 0xBB, 0xBF, 13, 10]);
        if (count == 2) File.WriteAllBytes(Path.Combine(tools, "a.ps1"), [0, 1, 10]);
        File.WriteAllText(Path.Combine(tools, "notes.txt"), "ignored");
        File.WriteAllText(Path.Combine(tools, "x.ps1x"), "ignored");
        File.WriteAllText(Path.Combine(tools, "bin", "inner.ps1"), "ignored");
        var output = Path.Combine(_root, "digest.json");
        File.WriteAllText(output, "stale output");
        var result = await Run(tools, output);
        Assert.True(result.ExitCode == 0, result.Stderr);
        var bytes = File.ReadAllBytes(output);
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        var text = File.ReadAllText(output);
        var expected = ExecutorToolsDigest.Compute(tools);
        Assert.NotNull(expected);
        Assert.Equal(expected, ExecutorToolsDigest.ReadManifest(text));
        using var json = JsonDocument.Parse(text);
        var files = json.RootElement.GetProperty("files");
        Assert.Equal(JsonValueKind.Array, files.ValueKind);
        Assert.Equal(count == 2 ? new[] { "a.ps1", "z.ps1" } : new[] { "z.ps1" },
            files.EnumerateArray().Select(file => file.GetString()));
        Assert.True(DateTimeOffset.TryParse(json.RootElement.GetProperty("utc").GetString(), out _));
        Assert.False(File.Exists(output + ".tmp"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Script_MissingOrEmptyFolder_FailsWithoutManifest(bool exists)
    {
        var tools = Path.Combine(_root, "tools");
        if (exists) Directory.CreateDirectory(tools);
        var output = Path.Combine(_root, "digest.json");
        var result = await Run(tools, output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".tmp"));
    }

    [Xunit.Fact]
    public void RepositoryTools_TrackedRuntimeAndDigestScript_ArePresent()
    {
        var tools = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "executor", "tools");
        Assert.NotNull(ExecutorToolsDigest.Compute(tools));
        Assert.True(File.Exists(Path.Combine(tools, "Get-ExecutorToolsDigest.ps1")));
        Assert.True(File.Exists(Path.Combine(tools, "Invoke-QueuedLanes.ps1")));
    }

    private async Task<(int ExitCode, string Stderr)> Run(string tools, string output)
    {
        var script = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "executor", "tools", "Get-ExecutorToolsDigest.ps1");
        Assert.True(File.Exists(script), "Tracked digest script is required for the process contract.");
        var start = new ProcessStartInfo(TestPowerShell.Executable)
        {
            WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        string[] allow = ["COMSPEC", "LOCALAPPDATA", "PATH", "PATHEXT", "PROGRAMDATA", "PROGRAMFILES",
            "PROGRAMFILES(X86)", "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "TMP", "WINDIR"];
        var required = start.Environment.Where(item => allow.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
        start.Environment.Clear();
        foreach (var item in required) start.Environment[item.Key] = item.Value;
        start.Environment["USERPROFILE"] = _root;
        foreach (var arg in new[] { "-NoProfile", "-File", script, "-Path", tools, "-OutFile", output })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Missing event: digest script process exit.");
        }
        await stdout;
        return (process.ExitCode, await stderr);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
