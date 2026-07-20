using System.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerShellTests
{
    [Xunit.Fact(DisplayName = "WorkerShell_resolves_a_powershell_host")]
    public void WorkerShellResolvesAPowerShellHost()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(Path.IsPathFullyQualified(WorkerShell.Executable));
            Assert.True(File.Exists(WorkerShell.Executable));
            Assert.False(WorkerShell.IsWindowsAppsPath(WorkerShell.Executable));
            return;
        }

        // Non-Windows resolves cross-platform pwsh when present, else leaves a concrete launch error.
        Assert.True(WorkerShell.Executable.EndsWith("pwsh", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "WorkerShell_base_arguments_end_with_command")]
    public void WorkerShellBaseArgumentsEndWithCommand()
    {
        var args = WorkerShell.BaseArguments();
        Assert.True(args.Any(a => a == "-NoProfile"));
        // -Command must be last so the wrapper/script follows it.
        Assert.Equal("-Command", args[^1]);
        // -ExecutionPolicy is Windows-only.
        Assert.Equal(OperatingSystem.IsWindows(), args.Any(a => a == "-ExecutionPolicy"));
    }

    [Xunit.Fact(DisplayName = "WorkerProcessRunner_stdin_encoding_does_not_emit_bom")]
    public void WorkerProcessRunnerStdinEncodingDoesNotEmitBom()
    {
        var startInfo = WorkerProcessRunner.BuildPowerShellStartInfo("Write-Output ok", Directory.GetCurrentDirectory());

        Assert.True(startInfo.RedirectStandardInput);
        Assert.Empty(startInfo.StandardInputEncoding?.GetPreamble() ?? []);
    }

    [Xunit.Fact(DisplayName = "WorkerProcessRunner_omits_stdin_encoding_when_stdin_is_not_redirected")]
    public void WorkerProcessRunnerOmitsStdinEncodingWhenStdinIsNotRedirected()
    {
        var startInfo = WorkerProcessRunner.BuildPowerShellStartInfo(
            "Write-Output ok",
            Directory.GetCurrentDirectory(),
            redirectStandardInput: false);

        Assert.False(startInfo.RedirectStandardInput);
        Assert.Null(startInfo.StandardInputEncoding);
    }
}
