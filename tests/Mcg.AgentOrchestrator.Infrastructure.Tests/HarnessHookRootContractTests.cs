using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class HarnessHookRootContractTests
{
    private const string LegacyBlockCommand =
        "pwsh -NoProfile -File \"$CLAUDE_PROJECT_DIR/.claude/hooks/Block-CompoundShell.ps1\"";

    [Xunit.Fact]
    public async Task LegacyCommand_WithoutRoot_ResolvesSlashPathAndFails()
    {
        using var fixture = HookWorkspace.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var command =
            "$env:CLAUDE_PROJECT_DIR = $null; Remove-Variable CLAUDE_PROJECT_DIR -ErrorAction SilentlyContinue; " +
            LegacyBlockCommand;

        var result = await WorkerProcessRunner.RunBufferedAsync(
            new WorkerProcessRunRequest(command, fixture.Root),
            timeout.Token);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("/.claude/hooks/Block-CompoundShell.ps1", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("not recognized as the name of a script file", result.StandardError, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task RenderedHookCommand_WithHarnessRoot_ExecutesIntendedScript()
    {
        using var fixture = HookWorkspace.Create();
        var command = WithoutOptionalEnvironment(ReadHookCommand(fixture.Root, "UserPromptSubmit"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = await WorkerProcessRunner.RunBufferedAsync(
            new WorkerProcessRunRequest(command, fixture.Root),
            timeout.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Current time:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Xunit.Fact]
    public async Task RenderedHookCommand_WithoutRoot_ExplainsRequiredContract()
    {
        using var fixture = HookWorkspace.Create();
        var command =
            "$env:CLAUDE_PROJECT_DIR = $null; " +
            ReadHookCommand(fixture.Root, "UserPromptSubmit");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = await WorkerProcessRunner.RunBufferedAsync(
            new WorkerProcessRunRequest(command, fixture.Root),
            timeout.Token);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Repository hook root is unavailable: set CLAUDE_PROJECT_DIR to the worktree root",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task RenderedCompoundHook_RejectsCompoundShellPayload()
    {
        using var fixture = HookWorkspace.Create();
        var command = WithoutOptionalEnvironment(ReadHookCommand(fixture.Root, "PreToolUse"));
        var benignPayload = JsonSerializer.Serialize(new { tool_input = new { command = "Get-ChildItem" } });
        var compoundPayload = JsonSerializer.Serialize(new { tool_input = new { command = "Get-ChildItem && Get-Date" } });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var benign = await WorkerProcessRunner.RunBufferedAsync(
            new WorkerProcessRunRequest(command, fixture.Root, StandardInput: benignPayload),
            timeout.Token);
        var blocked = await WorkerProcessRunner.RunBufferedAsync(
            new WorkerProcessRunRequest(command, fixture.Root, StandardInput: compoundPayload),
            timeout.Token);

        Assert.Equal(0, benign.ExitCode);
        Assert.Empty(benign.StandardError);
        Assert.Equal(2, blocked.ExitCode);
        Assert.Contains("BLOCKED compound shell command", blocked.StandardError, StringComparison.Ordinal);
        Assert.Contains("chaining &&", blocked.StandardError, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task RenderedCompoundHook_WithoutRoot_BlocksWithActionableDiagnostic()
    {
        using var fixture = HookWorkspace.Create();
        var command =
            "$env:CLAUDE_PROJECT_DIR = $null; " +
            ReadHookCommand(fixture.Root, "PreToolUse");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = await WorkerProcessRunner.RunBufferedAsync(
            new WorkerProcessRunRequest(command, fixture.Root),
            timeout.Token);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(
            "Repository hook root is unavailable: set CLAUDE_PROJECT_DIR to the worktree root",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ContractApplication_SeparateWorktrees_DoesNotMutateAmbientRoot()
    {
        using var first = HookWorkspace.Create();
        using var second = HookWorkspace.Create();
        var firstNested = Directory.CreateDirectory(Path.Combine(first.Root, "src", "nested")).FullName;
        var ambientBefore = Environment.GetEnvironmentVariable(HarnessHookRootContract.EnvironmentVariableName);

        var firstStartInfo = new ProcessStartInfo
        {
            WorkingDirectory = firstNested,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var secondStartInfo = new ProcessStartInfo
        {
            WorkingDirectory = second.Root,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var firstRoot = HarnessHookRootContract.Apply(firstStartInfo);
        var secondRoot = HarnessHookRootContract.Apply(secondStartInfo);

        Assert.Equal(first.Root.Replace('\\', '/'), firstRoot);
        Assert.Equal(second.Root.Replace('\\', '/'), secondRoot);
        Assert.NotEqual(firstRoot, secondRoot);
        Assert.Equal(firstRoot, firstStartInfo.Environment[HarnessHookRootContract.EnvironmentVariableName]);
        Assert.Equal(secondRoot, secondStartInfo.Environment[HarnessHookRootContract.EnvironmentVariableName]);
        Assert.Equal(ambientBefore, Environment.GetEnvironmentVariable(HarnessHookRootContract.EnvironmentVariableName));
    }

    [Xunit.Fact]
    public void ProviderSeed_UnsupportedHookRoot_WritesActionableDiagnostic()
    {
        using var fixture = HookWorkspace.Create();
        var missingHook = Path.Combine(fixture.Root, ".claude", "hooks", "Block-CompoundShell.ps1");
        File.Delete(missingHook);
        var stderrPath = Path.Combine(fixture.Root, "dispatch.err.log");
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        DispatchProcessHost.SeedProviderEnvironment(
            startInfo,
            WorkerSandboxProvider.Hermes,
            Path.Combine(fixture.Root, ".mcg-sandbox"),
            stderrPath);

        var diagnostic = File.ReadAllText(stderrPath);
        Assert.Contains("Repository hook root contract unsupported", diagnostic, StringComparison.Ordinal);
        Assert.Contains(HarnessHookRootContract.EnvironmentVariableName, diagnostic, StringComparison.Ordinal);
        Assert.Contains(missingHook, diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Launch the harness from a worktree", diagnostic, StringComparison.Ordinal);
    }

    private static string ReadHookCommand(string root, string eventName)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".claude", "settings.json")));
        return settings.RootElement
            .GetProperty("hooks")
            .GetProperty(eventName)[0]
            .GetProperty("hooks")[0]
            .GetProperty("command")
            .GetString()
            ?? throw new InvalidOperationException($"Hook command for '{eventName}' was empty.");
    }

    private static string WithoutOptionalEnvironment(string command) =>
        "Remove-Item Env:HERMES_HOME,Env:GROK_HOME,Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue; " + command;

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository root from source path '{sourceFilePath}'.");
    }

    private sealed class HookWorkspace : IDisposable
    {
        private HookWorkspace(string root) => Root = root;

        public string Root { get; }

        public static HookWorkspace Create()
        {
            var sourceRoot = FindRepositoryRoot();
            var root = Path.Combine(Path.GetTempPath(), "mcg hook root with spaces", Guid.NewGuid().ToString("n"));
            var hooks = Path.Combine(root, ".claude", "hooks");
            Directory.CreateDirectory(hooks);
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.Copy(
                Path.Combine(sourceRoot, ".claude", "settings.json"),
                Path.Combine(root, ".claude", "settings.json"));
            File.Copy(
                Path.Combine(sourceRoot, ".claude", "hooks", "Emit-Timestamp.ps1"),
                Path.Combine(hooks, "Emit-Timestamp.ps1"));
            File.Copy(
                Path.Combine(sourceRoot, ".claude", "hooks", "Block-CompoundShell.ps1"),
                Path.Combine(hooks, "Block-CompoundShell.ps1"));
            return new HookWorkspace(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
