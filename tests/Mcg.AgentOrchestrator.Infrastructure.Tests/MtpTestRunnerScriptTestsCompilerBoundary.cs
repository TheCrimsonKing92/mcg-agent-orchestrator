using System.Reflection;
using System.Text.Json;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsCompilerBoundary
{
    [Xunit.Theory]
    [Xunit.InlineData("powershell.exe")]
    [Xunit.InlineData("pwsh")]
    public void CompilerFrontendsPreserveLongCacheIdentityAndReapOwnedDirectories(string executable)
    {
        if (!OperatingSystem.IsWindows()) Xunit.Assert.Skip("Windows compiler boundary.");
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var tempRoot = sandbox.CreateStartupHookTempRoot(300);
        var result = Run(sandbox, tempRoot, executable, "Resolve-MtpFaultDialogStartupHook");
        Xunit.Assert.Equal(0, result.ExitCode);
        var hook = result.Stdout.Trim();
        Xunit.Assert.Equal(300, hook.Length);
        Xunit.Assert.StartsWith(tempRoot + Path.DirectorySeparatorChar, hook, StringComparison.Ordinal);
        var hookType = Assembly.Load(File.ReadAllBytes(hook)).GetType("StartupHook", throwOnError: true)!;
        var initialize = hookType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Xunit.Assert.NotNull(initialize);
        Xunit.Assert.Equal(typeof(void), initialize.ReturnType);
        Xunit.Assert.Empty(initialize.GetParameters());
        AssertCompilerDirectoriesReaped(sandbox);
    }

    [Xunit.Theory]
    [Xunit.InlineData("powershell.exe")]
    [Xunit.InlineData("pwsh")]
    public void CompilerFailureReapsCompilerAndCacheStaging(string executable)
    {
        if (!OperatingSystem.IsWindows()) Xunit.Assert.Skip("Windows compiler boundary.");
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var tempRoot = sandbox.CreateStartupHookTempRoot(300);
        var result = Run(sandbox, tempRoot, executable, """
            $script:MtpStartupHookSource = 'this is not valid C#'
            try { Resolve-MtpFaultDialogStartupHook; exit 77 }
            catch { Write-Output 'EXPECTED_COMPILER_FAILURE'; exit 0 }
            """);
        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("EXPECTED_COMPILER_FAILURE", result.Stdout, StringComparison.Ordinal);
        AssertCompilerDirectoriesReaped(sandbox);
        Xunit.Assert.Empty(Directory.GetDirectories(Path.Combine(tempRoot, "mcg-mtp-startup-hook", "v1")));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void StagingCleanupFailurePreservesCompilerFailureOrWinningPublication(bool publishWinner)
    {
        if (!OperatingSystem.IsWindows()) Xunit.Assert.Skip("Windows file-sharing boundary.");
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var tempRoot = sandbox.CreateStartupHookTempRoot(300);
        var result = Run(sandbox, tempRoot, "powershell.exe", """
            $script:originalCompiler = (Get-Command Write-MtpFaultDialogStartupHookAssembly).ScriptBlock
            $script:lockedStaging = $null
            function Write-MtpFaultDialogStartupHookAssembly {
                param($Destination)
                if ($script:publishWinner) {
                    & $script:originalCompiler -Destination $Destination
                    $digest = Get-MtpSha256Text -Text $script:MtpStartupHookSource
                    $cache = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($Destination))
                    $published = [IO.Path]::Combine($cache, $digest)
                    [void][IO.Directory]::CreateDirectory($published)
                    [IO.File]::Copy($Destination, [IO.Path]::Combine($published, $script:MtpStartupHookAssemblyName))
                    $marker = @{ schemaVersion=$script:MtpStartupHookSchemaVersion; sourceDigest=$digest; assemblyDigest=(Get-MtpSha256File -Path $Destination) }
                    [IO.File]::WriteAllText([IO.Path]::Combine($published, $script:MtpStartupHookMarkerName), ($marker | ConvertTo-Json -Compress))
                }
                else { [IO.File]::WriteAllBytes($Destination, [byte[]]@(0)) }
                $lockPath = [IO.Path]::Combine([IO.Path]::GetDirectoryName($Destination), 'cleanup-lock')
                $script:lockedStaging = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                if (-not $script:publishWinner) { throw 'CONTROLLED_COMPILER_FAILURE' }
            }
            try {
                try { $hook = Resolve-MtpFaultDialogStartupHook; @{ hook=$hook; error=$null } | ConvertTo-Json -Compress }
                catch { @{ hook=$null; error=$_.Exception.Message } | ConvertTo-Json -Compress }
            }
            finally { if ($null -ne $script:lockedStaging) { $script:lockedStaging.Dispose() } }
            """, publishWinner);
        Xunit.Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.Stdout);
        if (publishWinner)
        {
            Xunit.Assert.True(output.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null, result.Stdout + result.Stderr);
            var hook = output.RootElement.GetProperty("hook").GetString()!;
            Xunit.Assert.Equal(300, hook.Length);
            Xunit.Assert.True(File.Exists(hook));
        }
        else Xunit.Assert.Equal("CONTROLLED_COMPILER_FAILURE", output.RootElement.GetProperty("error").GetString());
        Xunit.Assert.Contains("Startup-hook staging cleanup failed", result.Stderr, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void CompilerCleanupFailurePreservesSuccessfulCopyOrPrimaryFailure(bool compilerFails)
    {
        if (!OperatingSystem.IsWindows()) Xunit.Assert.Skip("Windows file-sharing boundary.");
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var tempRoot = sandbox.CreateStartupHookTempRoot(300);
        var body = """
            $script:forceCompilerFailure = $FAILURE_FLAG
            $script:compilerLock = $null
            function Add-Type {
                param($TypeDefinition, $OutputAssembly, $OutputType, $ErrorAction)
                Microsoft.PowerShell.Utility\Add-Type @PSBoundParameters
                $lockPath = [IO.Path]::Combine([IO.Path]::GetDirectoryName($OutputAssembly), 'cleanup-lock')
                $script:compilerLock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                if ($script:forceCompilerFailure) { throw 'CONTROLLED_COMPILER_FAILURE' }
            }
            try {
                try { $hook = Resolve-MtpFaultDialogStartupHook; @{ hook=$hook; error=$null } | ConvertTo-Json -Compress }
                catch { @{ hook=$null; error=$_.Exception.Message } | ConvertTo-Json -Compress }
            }
            finally { if ($null -ne $script:compilerLock) { $script:compilerLock.Dispose() } }
            """.Replace("FAILURE_FLAG", compilerFails.ToString().ToLowerInvariant(), StringComparison.Ordinal);
        var result = Run(sandbox, tempRoot, TestPowerShell.Executable, body);
        Xunit.Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.Stdout);
        if (compilerFails)
            Xunit.Assert.Equal("CONTROLLED_COMPILER_FAILURE", output.RootElement.GetProperty("error").GetString());
        else
        {
            Xunit.Assert.True(output.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null, result.Stdout + result.Stderr);
            var hook = output.RootElement.GetProperty("hook").GetString()!;
            Xunit.Assert.Equal(300, hook.Length);
            Xunit.Assert.True(File.Exists(hook));
        }
        Xunit.Assert.Contains("Startup-hook compiler cleanup", result.Stderr, StringComparison.Ordinal);
        Xunit.Assert.Single(Directory.GetDirectories(Path.Combine(sandbox.LocalApplicationDataRoot, "Temp", "mcg-mtp-compile")));
    }

    private static void AssertCompilerDirectoriesReaped(MtpTestRunnerScriptTests.ScriptSandbox sandbox)
    {
        var compilerRoot = Path.Combine(sandbox.LocalApplicationDataRoot, "Temp", "mcg-mtp-compile");
        Xunit.Assert.True(Directory.Exists(compilerRoot), "The isolated compiler root must actually have been used.");
        Xunit.Assert.Empty(Directory.GetDirectories(compilerRoot));
    }

    private static MtpTestRunnerScriptTests.ProcessResult Run(
        MtpTestRunnerScriptTests.ScriptSandbox sandbox, string tempRoot, string executable, string body, bool publishWinner = false)
    {
        var start = sandbox.SandboxPowerShellStartInfo();
        start.FileName = TestPowerShell.ForTheoryToken(executable);
        start.Environment["TEMP"] = tempRoot;
        start.Environment["TMP"] = tempRoot;
        var module = Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1").Replace("'", "''", StringComparison.Ordinal);
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"$ErrorActionPreference='Stop'; $m=Import-Module '{module}' -Force -PassThru; & $m {{ $script:publishWinner=${publishWinner.ToString().ToLowerInvariant()}; {body} }}");
        return MtpTestRunnerScriptTests.Run(start);
    }
}
