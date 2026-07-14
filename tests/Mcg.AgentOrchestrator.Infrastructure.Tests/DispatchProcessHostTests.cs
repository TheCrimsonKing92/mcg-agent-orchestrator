using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("ProcessSpawning")]
public sealed class DispatchProcessHostTests
{
    [Xunit.Fact(DisplayName = "DispatchProcessHost_parameters_round_trip_via_camelCase_json")]
    public void DispatchProcessHostParametersRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "dispatch.json");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                dir,
                Path.Combine(dir, "out.log"),
                Path.Combine(dir, "err.log"),
                Path.Combine(dir, "exit.txt"),
                Path.Combine(dir, "heartbeat.json"),
                ShutdownBuildServerOnExit: true,
                DisableSharedCompilation: true,
                Provider: WorkerSandboxProvider.Codex,
                PromptPath: Path.Combine(dir, "prompt.md"));

            DispatchProcessHost.WriteParameters(path, parameters);

            var json = File.ReadAllText(path);
            // The detached host reads this with a camelCase policy, so the keys must be camelCase.
            Assert.True(json.Contains("\"command\"", StringComparison.Ordinal));
            Assert.True(json.Contains("\"disableSharedCompilation\"", StringComparison.Ordinal));
            Assert.True(json.Contains("\"promptPath\"", StringComparison.Ordinal));

            var roundTripped = JsonSerializer.Deserialize<DispatchProcessHost.DispatchRunParameters>(
                json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.Equal(parameters, roundTripped);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_writes_large_non_ascii_prompt_to_stdin_as_utf8_without_bom")]
    public void DispatchProcessHostWritesPromptToStdinAsUtf8()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-stdin-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var promptPath = Path.Combine(dir, "prompt.md");
            var prompt = new string('A', 32_000) + " CJK=漢字 emoji=🙂";
            File.WriteAllText(promptPath, prompt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var expected = File.ReadAllBytes(promptPath);
            using var stdin = new MemoryStream();
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "codex exec --cd repo",
                dir,
                Path.Combine(dir, "out.log"),
                Path.Combine(dir, "err.log"),
                Path.Combine(dir, "exit.txt"),
                null,
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false,
                Provider: WorkerSandboxProvider.Codex,
                PromptPath: promptPath);

            Assert.True(DispatchProcessHost.ShouldWritePromptToStdin(parameters));
            DispatchProcessHost.WriteUtf8PromptToStream(promptPath, stdin);

            var actual = stdin.ToArray();
            Assert.Equal(expected, actual);
            Assert.False(actual.Length >= 3 && actual[0] == 0xEF && actual[1] == 0xBB && actual[2] == 0xBF);
            Assert.Equal(prompt, Encoding.UTF8.GetString(actual));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_marks_worker_processes_with_dispatch_environment")]
    public void DispatchProcessHostMarksWorkerProcessesWithDispatchEnvironment()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-env-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var envPath = Path.Combine(dir, "worker-env.txt");
            var stdoutPath = Path.Combine(dir, "out.log");
            var stderrPath = Path.Combine(dir, "err.log");
            var exitPath = Path.Combine(dir, "exit.txt");
            var command = $"Set-Content -LiteralPath '{envPath}' -Value $env:{WorkerSandboxOptions.DispatchWorkerVariable}; Write-Output worker-env-captured";
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                command,
                dir,
                stdoutPath,
                stderrPath,
                exitPath,
                null,
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false,
                Provider: WorkerSandboxProvider.Codex);
            var parametersPath = Path.Combine(dir, "dispatch.json");
            DispatchProcessHost.WriteParameters(parametersPath, parameters);

            var result = DispatchProcessHost.Run(parametersPath);

            Assert.Equal(0, result);
            Assert.Equal("0", File.ReadAllText(exitPath).Trim());
            Assert.Equal("1", File.ReadAllText(envPath).Trim());
            var stdout = File.ReadAllText(stdoutPath);
            Assert.True(stdout.Contains("worker-env-captured", StringComparison.Ordinal), stdout);
            Assert.True(string.IsNullOrWhiteSpace(File.ReadAllText(stderrPath)), File.ReadAllText(stderrPath));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_child_receives_prompt_stdin_bytes_and_eof_without_paid_worker")]
    public void DispatchProcessHostChildReceivesPromptStdinBytesAndEofWithoutPaidWorker()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-child-stdin-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var promptPath = Path.Combine(dir, "prompt.md");
            var capturePath = Path.Combine(dir, "stdin-capture.json");
            var stdoutPath = Path.Combine(dir, "out.log");
            var stderrPath = Path.Combine(dir, "err.log");
            var exitPath = Path.Combine(dir, "exit.txt");
            var shimPath = Path.Combine(dir, "codex-stdin-shim.ps1");
            var prompt = new string('X', 32_000) + " CJK=漢字 emoji=🙂 eof=done";
            File.WriteAllText(promptPath, prompt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(
                shimPath,
                """
                param([string]$CapturePath)
                $inputStream = [Console]::OpenStandardInput()
                $buffer = New-Object byte[] 8192
                $memory = [System.IO.MemoryStream]::new()
                while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $memory.Write($buffer, 0, $read)
                }
                $bytes = $memory.ToArray()
                $sha = [System.Security.Cryptography.SHA256]::Create()
                $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
                $text = [System.Text.Encoding]::UTF8.GetString($bytes)
                $start = [Math]::Max(0, $text.Length - 64)
                [pscustomobject]@{
                    byteCount = $bytes.Length
                    sha256 = $hash
                    textTail = $text.Substring($start)
                    eofObserved = $true
                } | ConvertTo-Json -Compress | Set-Content -LiteralPath $CapturePath -Encoding UTF8
                Write-Output "stdin-eof-observed"
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var expectedBytes = File.ReadAllBytes(promptPath);
            var expectedHash = Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant();
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                $"& '{shimPath}' '{capturePath}'",
                dir,
                stdoutPath,
                stderrPath,
                exitPath,
                null,
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false,
                Provider: WorkerSandboxProvider.Codex,
                PromptPath: promptPath);
            var parametersPath = Path.Combine(dir, "dispatch.json");
            DispatchProcessHost.WriteParameters(parametersPath, parameters);

            var result = DispatchProcessHost.Run(parametersPath);

            Assert.Equal(0, result);
            Assert.Equal("0", File.ReadAllText(exitPath).Trim());
            var stdout = File.ReadAllText(stdoutPath);
            Assert.True(stdout.Contains("stdin-eof-observed", StringComparison.Ordinal), stdout);
            Assert.True(File.Exists(capturePath), File.ReadAllText(stderrPath));
            using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
            var root = capture.RootElement;
            Assert.Equal(expectedBytes.Length, root.GetProperty("byteCount").GetInt32());
            Assert.Equal(expectedHash, root.GetProperty("sha256").GetString());
            var textTail = root.GetProperty("textTail").GetString();
            Assert.NotNull(textTail);
            Assert.True(textTail.Contains("CJK=漢字 emoji=🙂 eof=done", StringComparison.Ordinal), textTail);
            Assert.True(root.GetProperty("eofObserved").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_low_integrity_path_removes_windowsapps_and_prepends_shell_dir")]
    public void LowIntegrityPathRemovesWindowsAppsAndPrependsShellDir()
    {
        var shellDir = Path.Combine(Path.GetTempPath(), "real-powershell");
        var shell = Path.Combine(shellDir, OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");
        var windowsApps = Path.Combine(Path.GetTempPath(), "Microsoft", "WindowsApps");
        var toolDir = Path.Combine(Path.GetTempPath(), "tooling");
        var originalPath = string.Join(Path.PathSeparator, windowsApps, toolDir, shellDir);

        var result = DispatchProcessHost.BuildLowIntegrityPath(originalPath, shell);
        var entries = result.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(shellDir, entries[0]);
        Assert.Contains(toolDir, entries);
        Assert.DoesNotContain(entries, DispatchProcessHost.IsWindowsAppsPathSegment);
        Assert.Equal(1, entries.Count(entry => string.Equals(entry, shellDir, StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_low_integrity_path_prepends_sandbox_bin_before_shell_dir")]
    public void LowIntegrityPathPrependsSandboxBinBeforeShellDir()
    {
        var shellDir = Path.Combine(Path.GetTempPath(), "real-powershell");
        var shell = Path.Combine(shellDir, OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");
        var sandboxBin = Path.Combine(Path.GetTempPath(), "mcg-sandbox-bin");
        var toolDir = Path.Combine(Path.GetTempPath(), "tooling");

        var result = DispatchProcessHost.BuildLowIntegrityPath(toolDir, shell, sandboxBin);
        var entries = result.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(sandboxBin, entries[0]);
        Assert.Equal(shellDir, entries[1]);
        Assert.Contains(toolDir, entries);
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_writes_git_and_dotnet_shims_with_resolved_absolute_paths_and_retry_evidence")]
    public void DispatchProcessHostWritesGitAndDotnetShims()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mcg-shim-generation-test", Guid.NewGuid().ToString("n"));
        var toolDir = Path.Combine(dir, "tools");
        var sandboxBin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(toolDir);
        Directory.CreateDirectory(sandboxBin);
        var realGit = Path.Combine(toolDir, "git.exe");
        var realDotnet = Path.Combine(toolDir, "dotnet.exe");
        File.WriteAllText(realGit, string.Empty);
        File.WriteAllText(realDotnet, string.Empty);
        try
        {
            DispatchProcessHost.WriteWorkerCommandShims(sandboxBin, toolDir);

            var gitShim = Path.Combine(sandboxBin, "git.cmd");
            var dotnetShim = Path.Combine(sandboxBin, "dotnet.cmd");
            Assert.True(File.Exists(gitShim));
            Assert.True(File.Exists(dotnetShim));
            Assert.Contains(realGit, File.ReadAllText(gitShim));
            Assert.Contains(realDotnet, File.ReadAllText(dotnetShim));
            Assert.Contains("CreateProcessAsUserW 1312", File.ReadAllText(gitShim));
            Assert.Contains("specified logon session does not exist", File.ReadAllText(dotnetShim));
            Assert.Contains("\"!MCG_ATTEMPT!\"==\"3\"", File.ReadAllText(gitShim));
            Assert.Contains("Start-Sleep -Milliseconds 250", File.ReadAllText(gitShim));
            Assert.Contains("Start-Sleep -Milliseconds 750", File.ReadAllText(gitShim));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_creates_bin_shims_and_prepends_child_path_only")]
    public void ApplyWorkerSandboxCreatesBinShimsAndPrependsChildPathOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-apply-sandbox-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(root);
        var hostPathBefore = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Directory.CreateDirectory(worktree);
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = worktree
            };
            startInfo.ArgumentList.Add("Write-Output ok");
            var childPathBefore = startInfo.Environment["PATH"];
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                worktree,
                Path.Combine(root, "out.log"),
                Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"),
                null,
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false,
                SandboxLowIntegrity: true,
                WorkerSandboxProvider.Codex);

            DispatchProcessHost.ApplyWorkerSandbox(
                startInfo,
                parameters,
                new WorkerSandboxPreparer(new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true))));

            var sandboxBin = Path.Combine(worktree, ".mcg-sandbox", "bin");
            Assert.True(Directory.Exists(sandboxBin));
            Assert.True(File.Exists(Path.Combine(sandboxBin, "git.cmd")));
            Assert.True(File.Exists(Path.Combine(sandboxBin, "dotnet.cmd")));
            Assert.NotNull(childPathBefore);
            Assert.True(startInfo.Environment["PATH"].StartsWith(sandboxBin, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(hostPathBefore, Environment.GetEnvironmentVariable("PATH"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_scopes_codex_home_to_codex_provider")]
    public void ApplyWorkerSandboxScopesCodexHomeToCodexProvider()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-provider-sandbox-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(worktree);
            var startInfo = CreateSandboxStartInfo(worktree);
            var parameters = CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Claude);

            DispatchProcessHost.ApplyWorkerSandbox(
                startInfo,
                parameters,
                new WorkerSandboxPreparer(new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true))));

            var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
            Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
            Assert.True(Directory.Exists(Path.Combine(sandboxRoot, "temp")));
            Assert.True(Directory.Exists(Path.Combine(sandboxRoot, "bin")));
            Assert.Equal(Path.Combine(sandboxRoot, "claude-config"), startInfo.Environment["CLAUDE_CONFIG_DIR"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_leaves_unknown_provider_without_provider_home")]
    public void ApplyWorkerSandboxLeavesUnknownProviderWithoutProviderHome()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-unknown-provider-sandbox-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(worktree);
            var startInfo = CreateSandboxStartInfo(worktree);
            var parameters = CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Unknown);

            DispatchProcessHost.ApplyWorkerSandbox(
                startInfo,
                parameters,
                new WorkerSandboxPreparer(new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true))));

            var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
            Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "claude-config")));
            Assert.True(Directory.Exists(Path.Combine(sandboxRoot, "temp")));
            Assert.True(Directory.Exists(Path.Combine(sandboxRoot, "bin")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_skips_recursive_icacls_when_roots_are_prepared")]
    public void WorkerSandboxPreparerSkipsRecursiveIcaclsWhenRootsArePrepared()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-sandbox-preparer-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        Directory.CreateDirectory(sandboxRoot);
        WorkerSandboxPreparer.WritePreparationFiles(worktree, worktree, sandboxRoot);
        WorkerSandboxPreparer.WritePreparationFiles(sandboxRoot, worktree, sandboxRoot);
        var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true));
        try
        {
            var result = new WorkerSandboxPreparer(labeler).Prepare(worktree, sandboxRoot);

            Assert.False(result.WorktreeRecursiveRelabel);
            Assert.False(result.SandboxRecursiveRelabel);
            Assert.True(result.PrepReceiptHit);
            Assert.Empty(labeler.SetCalls);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_second_prepare_on_same_worktree_is_bounded_and_idempotent")]
    public void WorkerSandboxPreparerSecondPrepareOnSameWorktreeIsBoundedAndIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-sandbox-preparer-reuse-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true));
        try
        {
            var preparer = new WorkerSandboxPreparer(labeler);
            var first = preparer.Prepare(worktree, sandboxRoot);
            labeler.SetCalls.Clear();

            var second = preparer.Prepare(worktree, sandboxRoot);

            Assert.True(first.WorktreeRecursiveRelabel);
            Assert.False(first.SandboxRecursiveRelabel);
            Assert.False(second.WorktreeRecursiveRelabel);
            Assert.False(second.SandboxRecursiveRelabel);
            Assert.False(first.PrepReceiptHit);
            Assert.True(second.PrepReceiptHit);
            Assert.Empty(labeler.SetCalls);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_receipt_hit_skips_protection_subphases")]
    public void ApplyWorkerSandboxReceiptHitSkipsProtectionSubphases()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-apply-sandbox-receipt-hit-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true, Medium: true));
        try
        {
            Directory.CreateDirectory(root);
            var preparer = new WorkerSandboxPreparer(labeler);
            var protectedPhases = new List<string>();
            var sourceBundle = Path.Combine(root, "source-ca.pem");
            File.WriteAllText(sourceBundle, "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n");
            var firstStartInfo = CreateSandboxStartInfo(worktree);
            firstStartInfo.Environment["SSL_CERT_FILE"] = sourceBundle;
            var first = DispatchProcessHost.ApplyWorkerSandbox(
                firstStartInfo,
                CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Unknown),
                preparer,
                protectWorkspaceBoundary: _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase),
                protectGitMetadata: _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectGitMetadataPhase));
            Assert.False(first.PrepReceiptHit);
            Assert.Contains(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, protectedPhases);
            Assert.Contains(WorkerSandboxPreparer.ProtectGitMetadataPhase, protectedPhases);
            labeler.SetCalls.Clear();
            protectedPhases.Clear();

            var startInfo = CreateSandboxStartInfo(worktree);
            startInfo.Environment["SSL_CERT_FILE"] = sourceBundle;
            var parameters = CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Unknown);
            var phases = new List<string>();

            var second = DispatchProcessHost.ApplyWorkerSandbox(
                startInfo,
                parameters,
                preparer,
                (phase, _, _) => phases.Add(phase),
                _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase),
                _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectGitMetadataPhase));

            Assert.True(second.PrepReceiptHit);
            Assert.Empty(labeler.SetCalls);
            Assert.Empty(protectedPhases);
            AssertNoGitReceiptHitIntegrityQueries(labeler, worktree, root, sandboxRoot);
            Assert.Contains("prepare-roots", phases);
            Assert.Contains("receipt-fast-path", phases);
            Assert.Contains("materialize-sandbox", phases);
            Assert.DoesNotContain(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, phases);
            Assert.DoesNotContain(WorkerSandboxPreparer.ProtectGitMetadataPhase, phases);
            Assert.True(File.Exists(Path.Combine(sandboxRoot, "drop-to-low.ps1")));
            Assert.True(File.Exists(Path.Combine(sandboxRoot, DispatchProcessHost.LowIntegritySetupArtifactName)));
            Assert.Equal(Path.Combine(sandboxRoot, DispatchProcessHost.WorkerCaBundleFileName), startInfo.Environment["SSL_CERT_FILE"]);
            Assert.Equal(File.ReadAllText(sourceBundle), File.ReadAllText(startInfo.Environment["SSL_CERT_FILE"]));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_receipt_hit_reprotects_when_boundary_integrity_changed")]
    public void ApplyWorkerSandboxReceiptHitReprotectsWhenBoundaryIntegrityChanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-apply-sandbox-receipt-integrity-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true, Medium: true));
        try
        {
            Directory.CreateDirectory(root);
            var preparer = new WorkerSandboxPreparer(labeler);
            var protectedPhases = new List<string>();

            var first = DispatchProcessHost.ApplyWorkerSandbox(
                CreateSandboxStartInfo(worktree),
                CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Unknown),
                preparer,
                protectWorkspaceBoundary: _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase),
                protectGitMetadata: _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectGitMetadataPhase));
            Assert.False(first.PrepReceiptHit);
            Assert.Contains(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, protectedPhases);
            protectedPhases.Clear();
            labeler.SetCalls.Clear();
            labeler.QueryCalls.Clear();

            var receiptBefore = File.ReadAllText(Path.Combine(worktree, WorkerSandboxPreparer.ReceiptFileName));
            labeler.SetQueryState(root, new IntegrityLabelState(Exists: true, Low: true, Inheritable: true, Medium: false));

            var second = DispatchProcessHost.ApplyWorkerSandbox(
                CreateSandboxStartInfo(worktree),
                CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Unknown),
                preparer,
                protectWorkspaceBoundary: _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase),
                protectGitMetadata: _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectGitMetadataPhase));

            Assert.False(second.PrepReceiptHit);
            Assert.Empty(labeler.SetCalls);
            Assert.Contains(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, protectedPhases);
            Assert.DoesNotContain(WorkerSandboxPreparer.ProtectGitMetadataPhase, protectedPhases);
            AssertNoGitReceiptHitIntegrityQueries(labeler, worktree, root, sandboxRoot);
            Assert.NotEqual(receiptBefore, File.ReadAllText(Path.Combine(worktree, WorkerSandboxPreparer.ReceiptFileName)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_root_only_receipt_does_not_skip_protection")]
    public void ApplyWorkerSandboxRootOnlyReceiptDoesNotSkipProtection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-apply-sandbox-root-only-receipt-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true, Medium: true));
        try
        {
            CreateGitRepository(worktree);
            var preparer = new WorkerSandboxPreparer(labeler);
            var rootOnlyReceipt = preparer.Prepare(worktree, sandboxRoot);
            Assert.False(rootOnlyReceipt.PrepReceiptHit);
            labeler.SetCalls.Clear();

            var phases = new List<string>();
            var protectedPhases = new List<string>();
            var result = DispatchProcessHost.ApplyWorkerSandbox(
                CreateSandboxStartInfo(worktree),
                CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Unknown),
                preparer,
                (phase, _, _) => phases.Add(phase),
                _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase),
                _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectGitMetadataPhase));

            Assert.True(result.PrepReceiptHit);
            Assert.Empty(labeler.SetCalls);
            Assert.False(result.ReceiptCoversProtectionPhase(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase));
            Assert.False(result.ReceiptCoversProtectionPhase(WorkerSandboxPreparer.ProtectGitMetadataPhase));
            Assert.Contains(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, protectedPhases);
            Assert.Contains(WorkerSandboxPreparer.ProtectGitMetadataPhase, protectedPhases);
            Assert.Contains(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, phases);
            Assert.Contains(WorkerSandboxPreparer.ProtectGitMetadataPhase, phases);
            var statusAfterRootOnlyReceiptHit = GitStatus(worktree);
            Assert.DoesNotContain(".mcg-sandbox/", statusAfterRootOnlyReceiptHit);
            Assert.Empty(GitCli.FilterCommitWorthyStatus(statusAfterRootOnlyReceiptHit));

            protectedPhases.Clear();
            var completedResult = DispatchProcessHost.ApplyWorkerSandbox(
                CreateSandboxStartInfo(worktree),
                CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Unknown),
                preparer,
                protectWorkspaceBoundary: _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase),
                protectGitMetadata: _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectGitMetadataPhase));

            Assert.True(completedResult.PrepReceiptHit);
            Assert.True(completedResult.ReceiptCoversProtectionPhase(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase));
            Assert.True(completedResult.ReceiptCoversProtectionPhase(WorkerSandboxPreparer.ProtectGitMetadataPhase));
            Assert.Empty(protectedPhases);
            var statusAfterCompletedReceiptHit = GitStatus(worktree);
            Assert.DoesNotContain(".mcg-sandbox/", statusAfterCompletedReceiptHit);
            Assert.Empty(GitCli.FilterCommitWorthyStatus(statusAfterCompletedReceiptHit));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_receipt_missing_phase_does_not_skip_that_phase")]
    public void ApplyWorkerSandboxReceiptMissingPhaseDoesNotSkipThatPhase()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-apply-sandbox-partial-receipt-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true, Medium: true));
        try
        {
            Directory.CreateDirectory(sandboxRoot);
            var coveredPhases = new[] { WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase };
            WorkerSandboxPreparer.WritePreparationFiles(worktree, worktree, sandboxRoot, coveredPhases);
            WorkerSandboxPreparer.WritePreparationFiles(sandboxRoot, worktree, sandboxRoot, coveredPhases);

            var startInfo = CreateSandboxStartInfo(worktree);
            var parameters = CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Unknown);
            var phases = new List<string>();
            var protectedPhases = new List<string>();

            var result = DispatchProcessHost.ApplyWorkerSandbox(
                startInfo,
                parameters,
                new WorkerSandboxPreparer(labeler),
                (phase, _, _) => phases.Add(phase),
                _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase),
                _ => protectedPhases.Add(WorkerSandboxPreparer.ProtectGitMetadataPhase));

            Assert.True(result.PrepReceiptHit);
            Assert.True(result.ReceiptCoversProtectionPhase(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase));
            Assert.False(result.ReceiptCoversProtectionPhase(WorkerSandboxPreparer.ProtectGitMetadataPhase));
            Assert.Empty(labeler.SetCalls);
            Assert.DoesNotContain(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, protectedPhases);
            Assert.Contains(WorkerSandboxPreparer.ProtectGitMetadataPhase, protectedPhases);
            Assert.DoesNotContain(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, phases);
            Assert.Contains(WorkerSandboxPreparer.ProtectGitMetadataPhase, phases);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_rejects_v1_prep_receipt")]
    public void WorkerSandboxPreparerRejectsV1PrepReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-sandbox-preparer-v1-receipt-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        Directory.CreateDirectory(sandboxRoot);
        try
        {
            WorkerSandboxPreparer.WriteMarker(worktree);
            WorkerSandboxPreparer.WriteMarker(sandboxRoot);
            WriteV1Receipt(worktree, worktree, sandboxRoot);
            WriteV1Receipt(sandboxRoot, worktree, sandboxRoot);
            var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true));

            var result = new WorkerSandboxPreparer(labeler).Prepare(worktree, sandboxRoot);

            Assert.False(result.PrepReceiptHit);
            Assert.Contains(labeler.SetCalls, call => call.Path == worktree);
            Assert.Contains(labeler.SetCalls, call => call.Path == sandboxRoot);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_SeedWorkerCaBundle_copies_existing_bundle_into_sandbox")]
    public void SeedWorkerCaBundleCopiesExistingBundleIntoSandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-ca-bundle-test", Guid.NewGuid().ToString("n"));
        var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
        Directory.CreateDirectory(root);
        try
        {
            var sourceBundle = Path.Combine(root, "source-ca.pem");
            var sourcePem = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n";
            File.WriteAllText(sourceBundle, sourcePem);
            var startInfo = new ProcessStartInfo { UseShellExecute = false };
            startInfo.Environment["SSL_CERT_FILE"] = sourceBundle;

            DispatchProcessHost.SeedWorkerCaBundle(startInfo, sandboxRoot);

            var expectedBundle = Path.Combine(sandboxRoot, DispatchProcessHost.WorkerCaBundleFileName);
            Assert.Equal(expectedBundle, startInfo.Environment["SSL_CERT_FILE"]);
            Assert.Equal(sourcePem, File.ReadAllText(expectedBundle));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_BuildPemCertificateBundle_deduplicates_certificates")]
    public void BuildPemCertificateBundleDeduplicatesCertificates()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=mcg-test-root",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var pem = DispatchProcessHost.BuildPemCertificateBundle([certificate, certificate]);

        Assert.Equal(1, CountOccurrences(pem, "-----BEGIN CERTIFICATE-----"));
        Assert.Contains("-----END CERTIFICATE-----", pem, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_reprep_succeeds_when_existing_worktree_is_already_labeled")]
    public void WorkerSandboxPreparerReprepSucceedsWhenExistingWorktreeIsAlreadyLabeled()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-sandbox-preparer-existing-labeled-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        Directory.CreateDirectory(sandboxRoot);
        var labeler = new RecordingIntegrityLabeler(
            new IntegrityLabelState(Exists: true, Low: true, Inheritable: true),
            setResult: false);
        try
        {
            var result = new WorkerSandboxPreparer(labeler).Prepare(worktree, sandboxRoot);

            Assert.False(result.RequiresRecovery);
            Assert.False(result.PrepReceiptHit);
            Assert.False(result.WorktreeRecursiveRelabel);
            Assert.False(result.SandboxRecursiveRelabel);
            Assert.Contains(labeler.SetCalls, call => call.Path == worktree && call.Recursive);
            Assert.Contains(labeler.SetCalls, call => call.Path == sandboxRoot && !call.Recursive);
            Assert.True(File.Exists(Path.Combine(worktree, WorkerSandboxPreparer.ReceiptFileName)));
            Assert.True(File.Exists(Path.Combine(sandboxRoot, WorkerSandboxPreparer.ReceiptFileName)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_reused_worktree_acl_failure_returns_typed_recovery_action")]
    public void WorkerSandboxPreparerReusedWorktreeAclFailureReturnsTypedRecoveryAction()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-sandbox-preparer-recovery-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        Directory.CreateDirectory(sandboxRoot);
        File.WriteAllText(Path.Combine(worktree, WorkerSandboxPreparer.MarkerFileName), "{}");
        var labeler = new RecordingIntegrityLabeler(
            new IntegrityLabelState(Exists: true, Low: false, Inheritable: false),
            setResult: false);
        try
        {
            var result = new WorkerSandboxPreparer(labeler).Prepare(worktree, sandboxRoot);

            var action = Assert.IsType<WorkerSandboxPrepRecoverableAction>(result.RecoveryAction!);
            Assert.Equal(worktree, action.FailedRoot);
            Assert.Equal(worktree, action.Worktree);
            Assert.Equal(sandboxRoot, action.SandboxRoot);
            Assert.True(action.RequiresRecursiveRemediation);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_fresh_worktree_acl_failure_throws")]
    public void WorkerSandboxPreparerFreshWorktreeAclFailureThrows()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-sandbox-preparer-fresh-failure-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        var labeler = new RecordingIntegrityLabeler(
            new IntegrityLabelState(Exists: true, Low: false, Inheritable: false),
            setResult: false);
        try
        {
            var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
                new WorkerSandboxPreparer(labeler).Prepare(worktree, sandboxRoot));

            Assert.True(exception.Message.Contains("Failed to apply inheritable Low integrity label", StringComparison.Ordinal));
            Assert.Single(labeler.SetCalls);
            Assert.True(labeler.SetCalls[0].Recursive);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_handles_partial_sandbox_root_without_recursive_relabel")]
    public void WorkerSandboxPreparerHandlesPartialSandboxRootWithoutRecursiveRelabel()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-sandbox-preparer-partial-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        Directory.CreateDirectory(sandboxRoot);
        WorkerSandboxPreparer.WritePreparationFiles(worktree, worktree, sandboxRoot);
        var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true));
        try
        {
            var result = new WorkerSandboxPreparer(labeler).Prepare(worktree, sandboxRoot);

            Assert.False(result.WorktreeRecursiveRelabel);
            Assert.False(result.SandboxRecursiveRelabel);
            Assert.Contains(labeler.SetCalls, call => call.Path == sandboxRoot && !call.Recursive && call.Level == "(OI)(CI)L");
            Assert.DoesNotContain(labeler.SetCalls, call => call.Path.Contains(".git", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(Path.Combine(sandboxRoot, WorkerSandboxPreparer.MarkerFileName)));
            Assert.True(File.Exists(Path.Combine(sandboxRoot, WorkerSandboxPreparer.ReceiptFileName)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_recreated_worktree_invalidates_prep_receipt")]
    public void WorkerSandboxPreparerRecreatedWorktreeInvalidatesPrepReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-sandbox-preparer-recreate-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true));
        try
        {
            var preparer = new WorkerSandboxPreparer(labeler);
            _ = preparer.Prepare(worktree, sandboxRoot);
            labeler.SetCalls.Clear();
            Directory.Delete(worktree, recursive: true);

            var second = preparer.Prepare(worktree, sandboxRoot);

            Assert.False(second.PrepReceiptHit);
            Assert.Contains(labeler.SetCalls, call => call.Path == worktree && call.Recursive);
            Assert.True(File.Exists(Path.Combine(worktree, WorkerSandboxPreparer.ReceiptFileName)));
            Assert.True(File.Exists(Path.Combine(sandboxRoot, WorkerSandboxPreparer.ReceiptFileName)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_preflight_invokes_git_and_dotnet_from_shimmed_path")]
    public void DispatchProcessHostPreflightInvokesShimmedGitAndDotnet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mcg-shim-preflight-test", Guid.NewGuid().ToString("n"));
        var sandboxBin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(sandboxBin);
        var marker = Path.Combine(dir, "marker.txt");
        WriteMarkerShim(Path.Combine(sandboxBin, "git.cmd"), marker, "git");
        WriteMarkerShim(Path.Combine(sandboxBin, "dotnet.cmd"), marker, "dotnet");
        try
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
            startInfo.Environment["PATH"] = sandboxBin;
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                dir,
                Path.Combine(dir, "out.log"),
                Path.Combine(dir, "err.log"),
                Path.Combine(dir, "exit.txt"),
                null,
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false,
                SandboxLowIntegrity: true);

            DispatchProcessHost.RunLowIntegrityLaunchPreflight(startInfo, parameters);

            var lines = File.ReadAllLines(marker);
            Assert.Contains("git --version", lines);
            Assert.Contains("dotnet --version", lines);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_preflight_does_not_inherit_dispatch_worker_marker")]
    public void DispatchProcessHostPreflightDoesNotInheritDispatchWorkerMarker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mcg-shim-preflight-env-test", Guid.NewGuid().ToString("n"));
        var sandboxBin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(sandboxBin);
        var marker = Path.Combine(dir, "marker.txt");
        WriteEnvironmentMarkerShim(Path.Combine(sandboxBin, "git.cmd"), marker, "git");
        WriteEnvironmentMarkerShim(Path.Combine(sandboxBin, "dotnet.cmd"), marker, "dotnet");
        try
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
            startInfo.Environment["PATH"] = sandboxBin;
            startInfo.Environment[WorkerSandboxOptions.DispatchWorkerVariable] = "1";
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                dir,
                Path.Combine(dir, "out.log"),
                Path.Combine(dir, "err.log"),
                Path.Combine(dir, "exit.txt"),
                null,
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false,
                SandboxLowIntegrity: true);

            DispatchProcessHost.RunLowIntegrityLaunchPreflight(startInfo, parameters);

            var lines = File.ReadAllLines(marker);
            Assert.Contains("git --version dispatch=", lines);
            Assert.Contains("dotnet --version dispatch=", lines);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_preflight_fails_when_shimmed_command_exits_nonzero")]
    public void DispatchProcessHostPreflightFailsWhenShimExitsNonZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mcg-shim-preflight-fail-test", Guid.NewGuid().ToString("n"));
        var sandboxBin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(sandboxBin);
        File.WriteAllText(Path.Combine(sandboxBin, "git.cmd"), "@echo off\r\nexit /b 9\r\n");
        File.WriteAllText(Path.Combine(sandboxBin, "dotnet.cmd"), "@echo off\r\nexit /b 0\r\n");
        try
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
            startInfo.Environment["PATH"] = sandboxBin;
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                dir,
                Path.Combine(dir, "out.log"),
                Path.Combine(dir, "err.log"),
                Path.Combine(dir, "exit.txt"),
                null,
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false,
                SandboxLowIntegrity: true);

            var ex = Assert.ThrowsAny<InvalidOperationException>(() =>
                DispatchProcessHost.RunLowIntegrityLaunchPreflight(startInfo, parameters));
            Assert.Contains("git --version", ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_writes_exit_file_when_grandchild_holds_pipe_after_worker_exits")]
    public void DispatchProcessHostWritesExitFileWhenGrandchildHoldsPipeAfterWorkerExits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-drain-test", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        Process? hostProcess = null;
        try
        {
            // Command: spawn a long-running grandchild (inheriting the pipe handles),
            // then the worker exits immediately. The dispatch host must time-out the
            // drain and write the exit-code file rather than blocking forever.
            var hangCommand = OperatingSystem.IsWindows()
                ? "$psi = [System.Diagnostics.ProcessStartInfo]::new('ping.exe', '-n 30 127.0.0.1'); $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; [void][System.Diagnostics.Process]::Start($psi); Write-Output 'done'; exit 0"
                : "$psi = [System.Diagnostics.ProcessStartInfo]::new('sleep', '60'); $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; [void][System.Diagnostics.Process]::Start($psi); Write-Output 'done'; exit 0";

            var parametersPath = Path.Combine(dir, "dispatch.json");
            var stdoutPath = Path.Combine(dir, "out.log");
            var stderrPath = Path.Combine(dir, "err.log");
            var exitCodePath = Path.Combine(dir, "exit.txt");

            DispatchProcessHost.WriteParameters(parametersPath, new DispatchProcessHost.DispatchRunParameters(
                hangCommand,
                dir,
                stdoutPath,
                stderrPath,
                exitCodePath,
                null,
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
            startInfo.ArgumentList.Add(DispatchProcessHost.SubcommandName);
            startInfo.ArgumentList.Add(parametersPath);

            hostProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start dispatch host.");

            // The exit file must appear within the drain timeout (~12 s) plus buffer.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!File.Exists(exitCodePath) && DateTimeOffset.UtcNow < deadline)
                Thread.Sleep(200);

            // Kill the process tree (including any grandchildren that inherited pipe handles)
            // and wait for the host to fully exit before reading exit.txt. This removes the
            // file-handle race where a lingering grandchild holds an inherited handle while
            // we read. FileShare.ReadWrite + retry in ReadExitCodeWithRetry covers any
            // remaining window.
            try { hostProcess.Kill(entireProcessTree: true); } catch { }
            try { hostProcess.WaitForExit(5000); } catch { }

            Assert.True(File.Exists(exitCodePath));
            Assert.Equal("0", ReadExitCodeWithRetry(exitCodePath));
        }
        finally
        {
            try { hostProcess?.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ShouldReapWorker_spares_buffering_workers_until_maxRuntime")]
    public void ShouldReapWorkerSparesBufferingWorkers()
    {
        var maxRuntime = TimeSpan.FromMinutes(60);
        var maxIdle = TimeSpan.FromMinutes(20);

        // A worker that has produced NO output (e.g. claude-cli -p buffers to the end) is NOT reaped
        // on the idle cap even past it — only maxRuntime bounds it. This is the buffering-worker fix.
        Assert.False(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(25), hasProducedOutput: false, maxRuntime, maxIdle));
        Assert.True(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(61), TimeSpan.FromMinutes(25), hasProducedOutput: false, maxRuntime, maxIdle));

        // A worker that streamed then went quiet past the idle cap IS reaped (stall detection preserved).
        Assert.True(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(25), hasProducedOutput: true, maxRuntime, maxIdle));
        Assert.False(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), hasProducedOutput: true, maxRuntime, maxIdle));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_true_when_cpu_grows_above_epsilon_with_flat_bytes")]
    public void HasProgressedDetectsCpuGrowthWhenBytesFlat()
    {
        // CPU grows from 0 to 100ms (> 50ms epsilon), bytes flat
        Assert.True(DispatchProcessHost.HasProgressed(0, 0, 0, 100, 50));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_false_when_both_cpu_and_bytes_are_flat")]
    public void HasProgressedNoProgressWhenFlat()
    {
        // Both CPU and bytes flat
        Assert.False(DispatchProcessHost.HasProgressed(0, 0, 0, 0, 50));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_false_when_cpu_growth_equals_epsilon_exactly")]
    public void HasProgressedCpuAtEpsilonIsNotProgress()
    {
        // Delta = 50ms exactly at epsilon — not strictly greater, so not progress
        Assert.False(DispatchProcessHost.HasProgressed(0, 0, 0, 50, 50));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_true_when_bytes_grow_regardless_of_cpu")]
    public void HasProgressedDetectsByteGrowth()
    {
        Assert.True(DispatchProcessHost.HasProgressed(0, 10, 0, 0, 50));
    }

    [Xunit.Fact(DisplayName = "WaitForIntegrityLabeler_kills_helper_when_timeout_expires")]
    public void WaitForIntegrityLabelerKillsTimedOutHelper()
    {
        using var process = StartLongRunningHelper();

        var completed = DispatchProcessHost.WaitForIntegrityLabeler(process, TimeSpan.FromMilliseconds(100));

        Assert.False(completed);
        Assert.True(process.HasExited);
    }

    [Xunit.Fact(DisplayName = "WaitForIntegrityLabeler_returns_true_when_helper_exits_nonzero")]
    public void WaitForIntegrityLabelerReturnsTrueWhenHelperExitsNonZero()
    {
        using var process = StartNonZeroHelper();

        var completed = DispatchProcessHost.WaitForIntegrityLabeler(process, TimeSpan.FromSeconds(5));

        Assert.True(completed);
        Assert.True(process.HasExited);
        Assert.NotEqual(0, process.ExitCode);
    }

    [Xunit.Fact(DisplayName = "IcaclsIntegrityLabeler_set_integrity_returns_false_when_helper_exits_nonzero")]
    public void IcaclsIntegrityLabelerSetIntegrityReturnsFalseWhenHelperExitsNonZero()
    {
        var labeler = new IcaclsIntegrityLabeler(_ => StartNonZeroHelper());
        var root = Path.Combine(Path.GetTempPath(), "mcg-icacls-nonzero-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var applied = labeler.SetIntegrity(root, "(OI)(CI)L", recursive: false);

            Assert.False(applied);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_low_integrity_setup_keeps_linked_worktree_git_file_medium")]
    public void LowIntegritySetupKeepsLinkedWorktreeGitFileMedium()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (GetCurrentProcessIntegrityRid() < MediumIntegrityRid)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-acl-test", Guid.NewGuid().ToString("n"));
        var repo = Path.Combine(root, "repo");
        var worktree = Path.Combine(root, "linked-worktree");
        Directory.CreateDirectory(root);
        try
        {
            CreateLinkedWorktree(repo, worktree);
            var gitFile = Path.Combine(worktree, ".git");
            var workerFile = Path.Combine(worktree, "worker.txt");
            File.WriteAllText(workerFile, "worker editable");
            var outsideWorkspaceFile = Path.Combine(root, "outside-workspace.txt");
            File.WriteAllText(outsideWorkspaceFile, "outside-protected");
            var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
            var codexHome = Path.Combine(sandboxRoot, "codex-home");
            var tempDir = Path.Combine(sandboxRoot, "temp");
            var sandboxBin = Path.Combine(sandboxRoot, "bin");
            var setupArtifact = Path.Combine(sandboxRoot, DispatchProcessHost.LowIntegritySetupArtifactName);
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            var parametersPath = Path.Combine(root, "dispatch.json");
            var stdoutPath = Path.Combine(logs, "out.log");
            var stderrPath = Path.Combine(logs, "err.log");
            var workspaceCreate = Path.Combine(worktree, "worker-created.txt");
            var workspaceDelete = Path.Combine(worktree, "worker-delete.txt");
            File.WriteAllText(workspaceDelete, "delete me");
            var command =
                $"Set-Content -LiteralPath '{EscapePowerShellSingleQuoted(workspaceCreate)}' -Value 'created'; " +
                $"Set-Content -LiteralPath '{EscapePowerShellSingleQuoted(workerFile)}' -Value 'modified'; " +
                $"Remove-Item -LiteralPath '{EscapePowerShellSingleQuoted(workspaceDelete)}'; " +
                $"try {{ Set-Content -LiteralPath '{EscapePowerShellSingleQuoted(outsideWorkspaceFile)}' -Value 'unexpected'; Write-Output 'outside-write-unexpected'; exit 7 }} " +
                "catch { Write-Output 'outside-write-denied' }; Write-Output sandbox-ready";

            DispatchProcessHost.WriteParameters(parametersPath, new DispatchProcessHost.DispatchRunParameters(
                command,
                worktree,
                stdoutPath,
                stderrPath,
                Path.Combine(logs, "exit.txt"),
                Path.Combine(logs, "heartbeat.json"),
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false,
                SandboxLowIntegrity: true,
                WorkerSandboxProvider.Codex));

            var exitCode = DispatchProcessHost.Run(parametersPath);

            Assert.Equal(0, exitCode);
            Assert.Contains("outside-write-denied", File.ReadAllText(stdoutPath));
            Assert.Equal("outside-protected", File.ReadAllText(outsideWorkspaceFile));
            Assert.True(GetMandatoryIntegrityRid(outsideWorkspaceFile) >= MediumIntegrityRid);
            Assert.Equal("created", File.ReadAllText(workspaceCreate).Trim());
            Assert.Equal("modified", File.ReadAllText(workerFile).Trim());
            Assert.False(File.Exists(workspaceDelete));
            Assert.True(File.Exists(gitFile));
            Assert.True(GetMandatoryIntegrityRid(gitFile) >= MediumIntegrityRid);
            Assert.Equal(LowIntegrityRid, GetMandatoryIntegrityRid(workerFile));
            Assert.Equal(LowIntegrityRid, GetMandatoryIntegrityRid(codexHome));
            Assert.Equal(LowIntegrityRid, GetMandatoryIntegrityRid(tempDir));
            Assert.Equal(LowIntegrityRid, GetMandatoryIntegrityRid(sandboxBin));
            Assert.True(File.Exists(Path.Combine(sandboxBin, "git.cmd")));
            Assert.True(File.Exists(Path.Combine(sandboxBin, "dotnet.cmd")));
            Assert.True(File.Exists(setupArtifact));

            using (var setup = JsonDocument.Parse(File.ReadAllText(setupArtifact)))
            {
                var rootElement = setup.RootElement;
                Assert.Equal("prepared-root-inherited-low-integrity", rootElement.GetProperty("strategy").GetString());
                Assert.True(rootElement.GetProperty("worktreeRecursiveRelabel").GetBoolean());
                Assert.False(rootElement.GetProperty("sandboxRecursiveRelabel").GetBoolean());
            }

            var sandboxPrepEvents = File.ReadAllLines(stderrPath)
                .Where(line => line.Contains("\"event\":\"sandbox-prep\"", StringComparison.Ordinal))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            Assert.Contains(sandboxPrepEvents, evt => evt.GetProperty("phase").GetString() == "start");
            Assert.Contains(sandboxPrepEvents, evt =>
                evt.GetProperty("phase").GetString() == "complete" &&
                evt.TryGetProperty("elapsedMs", out var elapsedMs) &&
                elapsedMs.GetInt64() >= 0);

            var commonGitDir = RunGit(worktree, "rev-parse", "--git-common-dir");
            var commonGitDirPath = Path.IsPathRooted(commonGitDir)
                ? commonGitDir
                : Path.GetFullPath(Path.Combine(worktree, commonGitDir));
            Assert.True(GetMandatoryIntegrityRid(commonGitDirPath) >= MediumIntegrityRid);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static Process StartLongRunningHelper()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "ping.exe" : "sleep",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("30");
            startInfo.ArgumentList.Add("127.0.0.1");
        }
        else
        {
            startInfo.ArgumentList.Add("30");
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start long-running helper.");
    }

    private static Process StartNonZeroHelper()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("exit /b 5");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("exit 5");
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start nonzero helper.");
    }

    private static void CreateLinkedWorktree(string repo, string worktree)
    {
        Directory.CreateDirectory(repo);
        RunGit(repo, "init");
        RunGit(repo, "config", "user.email", "tests@example.invalid");
        RunGit(repo, "config", "user.name", "Tests");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
        RunGit(repo, "add", "seed.txt");
        RunGit(repo, "commit", "-m", "seed");
        RunGit(repo, "worktree", "add", "-b", "linked-test", worktree);
    }

    private static void CreateGitRepository(string worktree)
    {
        Directory.CreateDirectory(worktree);
        RunGit(worktree, "init");
        RunGit(worktree, "config", "user.email", "tests@example.invalid");
        RunGit(worktree, "config", "user.name", "Tests");
    }

    private static string GitStatus(string worktree) =>
        RunGit(worktree, "status", "--porcelain", "--untracked-files=all");

    private static ProcessStartInfo CreateSandboxStartInfo(string worktree)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = worktree
        };
        startInfo.ArgumentList.Add("Write-Output ok");
        return startInfo;
    }

    private static DispatchProcessHost.DispatchRunParameters CreateSandboxParameters(
        string root,
        string worktree,
        WorkerSandboxProvider provider)
    {
        return new DispatchProcessHost.DispatchRunParameters(
            "Write-Output ok",
            worktree,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "exit.txt"),
            null,
            ShutdownBuildServerOnExit: false,
            DisableSharedCompilation: false,
            SandboxLowIntegrity: true,
            provider);
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"git {string.Join(' ', arguments)} timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit {process.ExitCode}: {stderr}");
        }

        return stdout.Trim();
    }

    private static string EscapePowerShellSingleQuoted(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static void WriteMarkerShim(string path, string marker, string commandName)
    {
        var escapedMarker = marker.Replace("%", "%%", StringComparison.Ordinal);
        File.WriteAllText(
            path,
            $"""
            @echo off
            echo {commandName} %*>>"{escapedMarker}"
            exit /b 0
            """);
    }

    private static void WriteV1Receipt(string path, string worktree, string sandboxRoot)
    {
        var receipt = new
        {
            version = 1,
            path = Path.GetFullPath(path),
            worktree = Path.GetFullPath(worktree),
            sandboxRoot = Path.GetFullPath(sandboxRoot),
            skippedProtectionPhases = new[]
            {
                WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase,
                WorkerSandboxPreparer.ProtectGitMetadataPhase
            },
            verificationBasis = new[]
            {
                "receipt-schema-v1",
                "path-worktree-sandboxRoot-contentHash",
                "directory-creation-time",
                "low-integrity-marker",
                "low-inheritable-label"
            },
            contentHash = "legacy",
            preparedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        File.WriteAllText(
            Path.Combine(path, WorkerSandboxPreparer.ReceiptFileName),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + Environment.NewLine);
    }

    private static void WriteEnvironmentMarkerShim(string path, string marker, string commandName)
    {
        var escapedMarker = marker.Replace("%", "%%", StringComparison.Ordinal);
        File.WriteAllText(
            path,
            $"""
            @echo off
            echo {commandName} %* dispatch=%{WorkerSandboxOptions.DispatchWorkerVariable}%>>"{escapedMarker}"
            exit /b 0
            """);
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }

    private static void AssertNoGitReceiptHitIntegrityQueries(
        RecordingIntegrityLabeler labeler,
        string worktree,
        string workspaceBoundary,
        string sandboxRoot)
    {
        // Receipt-hit verification for a non-git temp worktree queries exactly the roots whose
        // receipt/protection state can invalidate the fast path: worktree Low/Inheritable,
        // workspace boundary Medium, and sandbox root Low/Inheritable. Git metadata adds queries
        // only when a .git file/directory exists.
        Assert.Equal([worktree, workspaceBoundary, sandboxRoot], labeler.QueryCalls);
    }

    private sealed class RecordingIntegrityLabeler(IntegrityLabelState queryState, bool setResult = true) : IWorkerIntegrityLabeler
    {
        public List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];

        public List<string> QueryCalls { get; } = [];

        private readonly Dictionary<string, IntegrityLabelState> queryStatesByPath = new(StringComparer.OrdinalIgnoreCase);

        public IntegrityLabelState Query(string path)
        {
            QueryCalls.Add(path);
            return queryStatesByPath.TryGetValue(path, out var state) ? state : queryState;
        }

        public void SetQueryState(string path, IntegrityLabelState state)
        {
            queryStatesByPath[path] = state;
        }

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            SetCalls.Add((path, level, recursive));
            return setResult;
        }
    }

    private const int LowIntegrityRid = 0x1000;
    private const int MediumIntegrityRid = 0x2000;

    private static int GetMandatoryIntegrityRid(string path)
    {
        var error = GetNamedSecurityInfo(
            path,
            1,
            0x00000010,
            out _,
            out _,
            out _,
            out _,
            out var securityDescriptor);
        if (error != 0)
        {
            throw new Win32Exception((int)error);
        }

        try
        {
            if (!GetSecurityDescriptorSacl(securityDescriptor, out var saclPresent, out var sacl, out _) ||
                !saclPresent ||
                sacl == IntPtr.Zero)
            {
                throw new InvalidOperationException($"No mandatory label SACL found for '{path}'.");
            }

            var aceCount = Marshal.ReadInt16(sacl, 4);
            for (var i = 0; i < aceCount; i++)
            {
                if (!GetAce(sacl, i, out var ace))
                {
                    continue;
                }

                if (Marshal.ReadByte(ace) != 0x11)
                {
                    continue;
                }

                var sid = IntPtr.Add(ace, 8);
                if (!ConvertSidToStringSid(sid, out var sidStringPtr))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    var sidString = Marshal.PtrToStringUni(sidStringPtr)
                        ?? throw new InvalidOperationException("Integrity SID was empty.");
                    var lastDash = sidString.LastIndexOf('-');
                    return int.Parse(sidString[(lastDash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                }
                finally
                {
                    LocalFree(sidStringPtr);
                }
            }

            throw new InvalidOperationException($"No mandatory label ACE found for '{path}'.");
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    private static int GetCurrentProcessIntegrityRid()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out var length);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, length, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var sid = Marshal.ReadIntPtr(buffer);
                if (!ConvertSidToStringSid(sid, out var sidStringPtr))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    var sidString = Marshal.PtrToStringUni(sidStringPtr)
                        ?? throw new InvalidOperationException("Token integrity SID was empty.");
                    var lastDash = sidString.LastIndexOf('-');
                    return int.Parse(sidString[(lastDash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                }
                finally
                {
                    LocalFree(sidStringPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string ReadExitCodeWithRetry(string path, int attempts = 5, int delayMs = 100)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException ex)
            {
                last = ex;
                if (i < attempts - 1) Thread.Sleep(delayMs);
            }
        }
        throw last!;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetNamedSecurityInfo(
        string pObjectName,
        int objectType,
        uint securityInfo,
        out IntPtr ppsidOwner,
        out IntPtr ppsidGroup,
        out IntPtr ppDacl,
        out IntPtr ppSacl,
        out IntPtr ppSecurityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr pSecurityDescriptor,
        out bool lpbSaclPresent,
        out IntPtr pSacl,
        out bool lpbSaclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetAce(IntPtr pAcl, int dwAceIndex, out IntPtr pAce);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
