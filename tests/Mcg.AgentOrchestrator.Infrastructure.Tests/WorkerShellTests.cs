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

    [Xunit.Fact(DisplayName = "WorkerShell_prefers_the_localappdata_variable_over_the_redirected_known_folder")]
    public void WorkerShellPrefersTheLocalAppDataVariableOverTheRedirectedKnownFolder()
    {
        // Reproduces the acceptance gate's hermetic environment: USERPROFILE is repointed at an empty profile
        // root, so GetFolderPath(LocalApplicationData) expands to a directory that has never held a
        // PowerShell install, while the LOCALAPPDATA variable still carries the real per-user location.
        const string RealLocalAppData = @"C:\Users\real\AppData\Local";
        const string RedirectedLocalAppData = @"C:\Temp\mcg-hvp\AppData\Local";

        var candidates = WorkerShell.WindowsPowerShellCandidates(
            localAppDataVariable: RealLocalAppData,
            localAppDataKnownFolder: RedirectedLocalAppData,
            programFiles: @"C:\Program Files",
            programW6432: @"C:\Program Files").ToArray();

        Assert.Equal(
            Path.Combine(RealLocalAppData, "Programs", "PowerShell", "7", "pwsh.exe"),
            candidates[0]);

        // The redirected known folder must still be offered, just never ahead of the real one - a host whose
        // LOCALAPPDATA variable is unset relies on it.
        Assert.Contains(
            Path.Combine(RedirectedLocalAppData, "Programs", "PowerShell", "7", "pwsh.exe"),
            candidates);

        // Windows PowerShell 5.1 must never be a candidate: it is a different MAJOR VERSION whose
        // ProcessStartInfo lacks ArgumentList, and every candidate here outranks the PATH search.
        Assert.DoesNotContain(candidates, candidate =>
            candidate.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase) ||
            candidate.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "WorkerShell_falls_back_to_the_known_folder_when_the_localappdata_variable_is_absent")]
    public void WorkerShellFallsBackToTheKnownFolderWhenTheLocalAppDataVariableIsAbsent()
    {
        const string KnownFolder = @"C:\Users\real\AppData\Local";

        var candidates = WorkerShell.WindowsPowerShellCandidates(
            localAppDataVariable: null,
            localAppDataKnownFolder: KnownFolder,
            programFiles: null,
            programW6432: null).ToArray();

        Assert.Equal(
            Path.Combine(KnownFolder, "Programs", "PowerShell", "7", "pwsh.exe"),
            candidates[0]);
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
