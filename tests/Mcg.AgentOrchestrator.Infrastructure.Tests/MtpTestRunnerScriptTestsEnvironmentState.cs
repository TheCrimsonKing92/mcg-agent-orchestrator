using System.Diagnostics;
using System.Text.Json;

// All environment mutations live in an owned child, never in the test host.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsEnvironmentState(ITestOutputHelper output)
{
    [Fact]
    public void Scrub_DisallowedStates_RemovesKeysAndPreservesInputs()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var nonempty = "MCG_ENV_PUBLIC_NONEMPTY_" + suffix;
        var empty = "MCG_ENV_PUBLIC_EMPTY_" + suffix;
        var input = "MCG_TEST_INPUT_ENV_" + suffix;
        var dotnet = "DOTNET_MTP_PUBLIC_" + suffix;
        var nuget = "NUGET_MTP_PUBLIC_" + suffix;
        string[] names = [nonempty, empty, input, "TEMP", dotnet, nuget];
        using var receipt = RunCase(sandbox, names, $$"""
            [Environment]::SetEnvironmentVariable('{{nonempty}}', 'inherited-public', 'Process')
            [Environment]::SetEnvironmentVariable('{{empty}}', '', 'Process')
            [Environment]::SetEnvironmentVariable('{{input}}', 'inherited-input', 'Process')
            [Environment]::SetEnvironmentVariable('{{dotnet}}', 'allowed-dotnet', 'Process')
            [Environment]::SetEnvironmentVariable('{{nuget}}', 'allowed-nuget', 'Process')
            $seeded = Read-PublicEnvironment $names
            $inputs = Assert-MtpTestInputEnvironment -TestInput @('{{input}}= explicit=public ')
            Set-MtpHermeticEnvironment -RepositoryRoot $root -WritableRoot $root -TestInputEnvironment $inputs
            @{ seeded = $seeded; local = (Read-PublicEnvironment $names); child = (Read-ChildEnvironment) }
            """);
        var seeded = receipt.RootElement.GetProperty("seeded");
        AssertState(seeded, nonempty, "inherited-public");
        AssertState(seeded, empty, "");
        AssertState(seeded, input, "inherited-input");
        foreach (var observation in Observations(receipt))
        {
            AssertState(observation, nonempty, null);
            AssertState(observation, empty, null);
            AssertState(observation, input, " explicit=public ");
            AssertState(observation, "TEMP", sandbox.Root);
            AssertState(observation, dotnet, "allowed-dotnet");
            AssertState(observation, nuget, "allowed-nuget");
        }
    }

    [Fact]
    public void Restore_OriginalStates_PreservesMembershipAndValues()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var prefix = "MCG_ENV_PUBLIC_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var absent = prefix + "_ABSENT";
        var empty = prefix + "_EMPTY";
        var nonempty = prefix + "_NONEMPTY";
        var temporary = prefix + "_TEMPORARY";
        string[] names = [absent, empty, nonempty, temporary];
        using var receipt = RunCase(sandbox, names, $$"""
            [Environment]::SetEnvironmentVariable('{{absent}}', [System.Management.Automation.Language.NullString]::Value, 'Process')
            [Environment]::SetEnvironmentVariable('{{temporary}}', [System.Management.Automation.Language.NullString]::Value, 'Process')
            [Environment]::SetEnvironmentVariable('{{empty}}', '', 'Process')
            [Environment]::SetEnvironmentVariable('{{nonempty}}', ' original=public ', 'Process')
            $seeded = Read-PublicEnvironment $names
            $snapshot = Get-MtpEnvironmentSnapshot
            $captured = @{ emptyPresent = $snapshot.ContainsKey('{{empty}}'); emptyValue = $snapshot['{{empty}}']; absentPresent = $snapshot.ContainsKey('{{absent}}') }
            foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, 'modified-public', 'Process') }
            $modified = Read-PublicEnvironment $names
            Restore-MtpEnvironment -Snapshot $snapshot
            @{ seeded = $seeded; captured = $captured; modified = $modified; local = (Read-PublicEnvironment $names); child = (Read-ChildEnvironment) }
            """);
        var seeded = receipt.RootElement.GetProperty("seeded");
        AssertState(seeded, absent, null);
        AssertState(seeded, temporary, null);
        AssertState(seeded, empty, "");
        AssertState(seeded, nonempty, " original=public ");
        var captured = receipt.RootElement.GetProperty("captured");
        Assert.True(captured.GetProperty("emptyPresent").GetBoolean(), "Snapshot lost public empty key membership.");
        Assert.Equal("", captured.GetProperty("emptyValue").GetString());
        Assert.False(captured.GetProperty("absentPresent").GetBoolean());
        foreach (var name in names)
            AssertState(receipt.RootElement.GetProperty("modified"), name, "modified-public");
        foreach (var observation in Observations(receipt))
        {
            AssertState(observation, absent, null);
            AssertState(observation, temporary, null);
            AssertState(observation, empty, "");
            AssertState(observation, nonempty, " original=public ");
        }
    }

    [Fact]
    public void Scrub_PublicGitIdentity_UsesExplicitConfiguration()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        string[] names = ["GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "EMAIL"];
        using var receipt = RunCase(sandbox, names, """
            $git = @(Get-Command git -CommandType Application -ErrorAction Stop)[0].Source
            foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, 'injected-public@example.invalid', 'Process') }
            $seeded = Read-PublicEnvironment $names
            Set-MtpHermeticEnvironment -RepositoryRoot $root -WritableRoot $root
            $local = Read-PublicEnvironment $names
            $child = Read-ChildEnvironment
            $identity = Invoke-OwnedChild -Executable $git -ChildArguments @('-c', 'user.name=BridgeFixture', '-c', 'user.email=fixture@example.invalid', 'var', 'GIT_AUTHOR_IDENT')
            @{ seeded = $seeded; local = $local; child = $child; gitExit = $identity.exitCode; identity = $identity.stdout.Trim() }
            """);
        foreach (var name in names)
        {
            AssertState(receipt.RootElement.GetProperty("seeded"), name, "injected-public@example.invalid");
            foreach (var observation in Observations(receipt))
                AssertState(observation, name, null);
        }
        Assert.Equal(0, receipt.RootElement.GetProperty("gitExit").GetInt32());
        Assert.Matches(@"^BridgeFixture <fixture@example\.invalid> \d+ [+-]\d{4}$",
            receipt.RootElement.GetProperty("identity").GetString()!);
    }

    private JsonDocument RunCase(MtpTestRunnerScriptTests.ScriptSandbox sandbox, string[] names, string body)
    {
        var nameArguments = "@(" + string.Join(",", names.Select(name => $"'{Quote(name)}'")) + ")";
        var probe = Path.Combine(sandbox.Root, "observe-environment.ps1");
        File.WriteAllText(probe, ObservationFunction + $"\nRead-PublicEnvironment {nameArguments} | ConvertTo-Json -Depth 6 -Compress\n");
        var script = Path.Combine(sandbox.Root, "environment-case.ps1");
        var module = Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Import-Module '{{Quote(module)}}' -Force -WarningAction SilentlyContinue
                & (Get-Module MtpTestRunner) {
                    $root = '{{Quote(sandbox.Root)}}'
                    $pwsh = [Environment]::ProcessPath
                    $probe = '{{Quote(probe)}}'
                    $names = {{nameArguments}}
                    {{ObservationFunction}}
                    {{OwnedChildFunctions}}
                    if ($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 10) {
                        throw 'Environment-state regression requires modern pwsh on .NET 10 or newer.'
                    }
                    & { {{body}} } | ConvertTo-Json -Depth 8 -Compress
                }
                exit 0
            } catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """);
        var start = new ProcessStartInfo
        {
            FileName = ResolvePwsh(),
            WorkingDirectory = sandbox.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Pin the launch context; observation children inherit the resulting map without overrides.
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        }
        start.Environment["LOCALAPPDATA"] = sandbox.LocalApplicationDataRoot;
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" })
            start.Environment[name] = sandbox.Root;
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            start.ArgumentList.Add(argument);
        var result = MtpTestRunnerScriptTests.Run(start, timeout: TimeSpan.FromMinutes(5));
        Assert.True(result.ExitCode == 0, $"Environment case exited {result.ExitCode}: {Bounded(result.Stdout + result.Stderr)}");
        var receipt = JsonDocument.Parse(result.Stdout);
        foreach (var observation in Observations(receipt))
        {
            var pwsh = observation.GetProperty("pwsh").GetString()!;
            var dotnet = observation.GetProperty("dotnet").GetString()!;
            output.WriteLine($"Environment-state runtime: pwsh={pwsh}; .NET={dotnet}");
            Assert.True(Version.Parse(pwsh.Split('-')[0]).Major >= 7, "Probe did not execute modern pwsh.");
            Assert.True(Version.Parse(dotnet).Major >= 10, "Probe did not execute the supported modern .NET runtime.");
        }
        return receipt;
    }

    private static IEnumerable<JsonElement> Observations(JsonDocument receipt)
    {
        yield return receipt.RootElement.GetProperty("local");
        yield return receipt.RootElement.GetProperty("child");
    }

    private static void AssertState(JsonElement observation, string name, string? expected)
    {
        var state = observation.GetProperty("states").GetProperty(name);
        Assert.True(state.GetProperty("present").GetBoolean() == (expected is not null),
            $"Public sentinel {name}: expected {(expected is null ? "absent" : "present")} membership.");
        Assert.Equal(expected, state.GetProperty("value").GetString());
    }

    private static string ResolvePwsh()
    {
        // Resolve once before scrubbing; never fall back to Windows PowerShell.
        var leaf = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim('"'), leaf);
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Environment-state regression requires pwsh on PATH; no Windows PowerShell fallback is permitted.");
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string Bounded(string value) => value.Length <= 4000 ? value : value[..4000];

    private const string ObservationFunction = """
        function Read-PublicEnvironment {
            param([string[]]$Names)
            $map = [Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Process)
            $states = [ordered]@{}
            foreach ($name in $Names) {
                $present = $map.Contains($name)
                $value = if ($present) { [string]$map[$name] } else { $null }
                $states[$name] = @{ present = $present; value = $value }
            }
            @{ pwsh = $PSVersionTable.PSVersion.ToString(); dotnet = [Environment]::Version.ToString(); states = $states }
        }
        """;

    private const string OwnedChildFunctions = """
        function Invoke-OwnedChild {
            param([string]$Executable, [string[]]$ChildArguments)
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $Executable
            $start.WorkingDirectory = $root
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            foreach ($argument in $ChildArguments) { $start.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $start
            $started = $false
            try {
                $started = $process.Start()
                if (-not $started) { throw 'Owned environment probe did not start.' }
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                # Hang guard only: the child must complete, with no elapsed-time assertion.
                if (-not $process.WaitForExit(120000)) { throw 'Owned environment probe did not exit before the hang guard.' }
                @{ exitCode = $process.ExitCode; stdout = $stdout.GetAwaiter().GetResult(); stderr = $stderr.GetAwaiter().GetResult() }
            } finally {
                if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(10000) }
                $process.Dispose()
            }
        }
        function Read-ChildEnvironment {
            $run = Invoke-OwnedChild -Executable $pwsh -ChildArguments @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $probe)
            if ($run.exitCode -ne 0) { throw 'Owned environment observation child failed.' }
            $run.stdout | ConvertFrom-Json -ErrorAction Stop
        }
        """;
}
