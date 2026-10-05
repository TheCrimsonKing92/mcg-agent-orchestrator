using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("ProcessSpawning")]
public sealed class DispatchProcessHostTests
{
    [Xunit.Fact]
    public void RoutineCompletionContract_ExistingHostSource_ExcludesGlobalShutdown()
    {
        var parameterNames = typeof(DispatchProcessHost.DispatchRunParameters)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        var sourcePath = Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "src",
            "Mcg.AgentOrchestrator.Execution",
            "Processes",
            "DispatchProcessHost.cs");
        var source = File.ReadAllText(sourcePath);
        var remediationSource = File.ReadAllText(Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "src",
            "Mcg.AgentOrchestrator.Infrastructure",
            "Workspaces",
            "DotnetBuildEnvironmentManager.cs"));

        Assert.DoesNotContain("ShutdownBuildServerOnExit", parameterNames);
        Assert.DoesNotContain("build-server", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ShutdownBuildServersBestEffort", remediationSource, StringComparison.Ordinal);
        Assert.Contains("ArgumentList = { \"build-server\", \"shutdown\" }", remediationSource, StringComparison.Ordinal);
    }

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
                DisableSharedCompilation: true,
                Provider: WorkerSandboxProvider.Codex,
                PromptPath: Path.Combine(dir, "prompt.md"));

            DispatchProcessHost.WriteParameters(path, parameters);

            var json = File.ReadAllText(path);
            // The detached host reads this with a camelCase policy, so the keys must be camelCase.
            Assert.True(json.Contains("\"command\"", StringComparison.Ordinal));
            Assert.True(json.Contains("\"disableSharedCompilation\"", StringComparison.Ordinal));
            Assert.True(json.Contains("\"promptPath\"", StringComparison.Ordinal));
            Assert.DoesNotContain("shutdownBuildServerOnExit", json, StringComparison.Ordinal);

            var roundTripped = JsonSerializer.Deserialize<DispatchProcessHost.DispatchRunParameters>(
                json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.Equal(parameters, roundTripped);

            var legacyJson = json.TrimEnd('}') + ",\"shutdownBuildServerOnExit\":true}";
            File.WriteAllText(path, legacyJson);
            var legacyRoundTripped = DispatchProcessHost.ReadParameters(path);
            Assert.Equal(parameters, legacyRoundTripped);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_omitted_output_drain_policy_json_uses_production_default")]
    public void DispatchProcessHostOmittedOutputDrainPolicyJsonUsesProductionDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "dispatch.json");
            var json = """
                {
                  "command": "Write-Output ok",
                  "workingDirectory": "C:\\tmp",
                  "stdoutPath": "C:\\tmp\\out.log",
                  "stderrPath": "C:\\tmp\\err.log",
                  "exitCodePath": "C:\\tmp\\exit.txt",
                  "heartbeatPath": null,
                  "disableSharedCompilation": false
                }
                """;
            File.WriteAllText(path, json);

            using var document = JsonDocument.Parse(json);
            Assert.False(document.RootElement.TryGetProperty("outputDrainPolicy", out _));

            var omitted = DispatchProcessHost.ReadParameters(path);
            Assert.Null(omitted.OutputDrainPolicy);
            var resolved = (omitted.OutputDrainPolicy ?? ProcessOutputDrainPolicy.Default).Validate();
            Assert.Equal(ProcessOutputDrainPolicy.Default, resolved);
            Assert.Equal(TimeSpan.FromSeconds(12), resolved.Completion);
            Assert.Equal(TimeSpan.FromSeconds(2), resolved.CancellationGrace);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_captures_provider_session_id_from_codex_output_lines")]
    public void DispatchProcessHostCapturesProviderSessionIdFromCodexOutputLines()
    {
        Assert.True(DispatchProcessHost.TryCaptureProviderSessionIdFromLine(
            """{"type":"session.started","id":"codex-json-session-123"}""",
            out var jsonSessionId));
        Assert.Equal("codex-json-session-123", jsonSessionId);

        Assert.True(DispatchProcessHost.TryCaptureProviderSessionIdFromLine(
            "Session ID: codex-text-session-456",
            out var textSessionId));
        Assert.Equal("codex-text-session-456", textSessionId);

        Assert.False(DispatchProcessHost.TryCaptureProviderSessionIdFromLine("tokens used: 1", out _));
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_session_capture_advances_offset_and_gives_up_after_64KiB")]
    public void DispatchProcessHostSessionCaptureAdvancesOffsetAndGivesUpAfterLimit()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "codex.stderr.log");
        File.WriteAllText(path, new string('x', DispatchProcessHost.ProviderSessionCaptureByteLimit + 4096));
        var state = new DispatchProcessHost.ProviderSessionCaptureState();

        var sessionId = DispatchProcessHost.TryCaptureProviderSessionIdFromLog(path, state);
        var offsetAfterFirstRead = state.Offset;
        var second = DispatchProcessHost.TryCaptureProviderSessionIdFromLog(path, state);

        Assert.Null(sessionId);
        Assert.Null(second);
        Assert.Equal(DispatchProcessHost.ProviderSessionCaptureByteLimit, offsetAfterFirstRead);
        Assert.Equal(offsetAfterFirstRead, state.Offset);
        Assert.True(state.GaveUp);
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_stderr_capture_exhaustion_does_not_disable_stdout_capture")]
    public void DispatchProcessHostStderrCaptureExhaustionDoesNotDisableStdoutCapture()
    {
        var root = CreateTempDirectory();
        var stdout = Path.Combine(root, "codex.stdout.log");
        var stderr = Path.Combine(root, "codex.stderr.log");
        File.WriteAllText(stdout, "Session ID: stdout-session-after-stderr-cap");
        File.WriteAllText(stderr, string.Empty);
        var stdoutState = new DispatchProcessHost.ProviderSessionCaptureState();
        var stderrState = new DispatchProcessHost.ProviderSessionCaptureState
        {
            GaveUp = true
        };

        var sessionId = DispatchProcessHost.TryCaptureProviderSessionId(
            WorkerSandboxProvider.Codex,
            stdout,
            stderr,
            stdoutState,
            stderrState);

        Assert.Equal("stdout-session-after-stderr-cap", sessionId);
        Assert.False(stdoutState.GaveUp);
        Assert.True(stderrState.GaveUp);
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
                DisableSharedCompilation: false,
                Provider: WorkerSandboxProvider.Codex,
                PromptPath: promptPath);

            Assert.True(DispatchProcessHost.ShouldWritePromptToStdin(parameters));
            Assert.True(DispatchProcessHost.ShouldWritePromptToStdin(parameters with
            {
                Provider = WorkerSandboxProvider.Ollama
            }));
            Assert.False(DispatchProcessHost.ShouldWritePromptToStdin(parameters with
            {
                Provider = WorkerSandboxProvider.Grok
            }));
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
                DisableSharedCompilation: false,
                Provider: WorkerSandboxProvider.Codex);
            var parametersPath = Path.Combine(dir, "dispatch.json");
            DispatchProcessHost.WriteParameters(parametersPath, parameters);

            var result = DispatchProcessHost.Run(parametersPath);

            Assert.Equal(0, result);
            AssertNativeExitArtifact(exitPath, 0);
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
                DisableSharedCompilation: false,
                Provider: WorkerSandboxProvider.Codex,
                PromptPath: promptPath);
            var parametersPath = Path.Combine(dir, "dispatch.json");
            DispatchProcessHost.WriteParameters(parametersPath, parameters);

            var result = DispatchProcessHost.Run(parametersPath);

            Assert.Equal(0, result);
            AssertNativeExitArtifact(exitPath, 0);
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

    [Xunit.Fact(DisplayName = "DispatchProcessHost_records_selected_child_exit_separately_from_root_exit")]
    public void DispatchProcessHostRecordsSelectedChildExitSeparately()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-child-exit-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        Task<int>? runTask = null;
        int? heartbeatChildPid = null;
        var gatePath = Path.Combine(dir, "release-child");
        var readyPath = Path.Combine(dir, "child-ready");
        try
        {
            var stdoutPath = Path.Combine(dir, "out.log");
            var stderrPath = Path.Combine(dir, "err.log");
            var exitPath = Path.Combine(dir, "exit.txt");
            var childExitPath = Path.Combine(dir, "child-exit.json");
            var heartbeatPath = Path.Combine(dir, "heartbeat.json");
            var childScriptPath = Path.Combine(dir, "silent-child.ps1");
            var shell = EscapePowerShellSingleQuoted(WorkerShell.Executable);
            File.WriteAllText(
                childScriptPath,
                $"[IO.File]::WriteAllText('{EscapePowerShellSingleQuoted(readyPath)}', [string]$PID); while (!(Test-Path -LiteralPath '{EscapePowerShellSingleQuoted(gatePath)}')) {{ [void][Math]::Sqrt(1234567) }}{Environment.NewLine}exit 23",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var command =
                $"& '{shell}' -NoProfile -NonInteractive -InputFormat None -File " +
                $"'{EscapePowerShellSingleQuoted(childScriptPath)}'; exit 1";
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                command,
                dir,
                stdoutPath,
                stderrPath,
                exitPath,
                heartbeatPath,
                DisableSharedCompilation: false,
                Provider: WorkerSandboxProvider.Claude,
                ChildExitRecordPath: childExitPath,
                HeartbeatIntervalMilliseconds: 25);
            var parametersPath = Path.Combine(dir, "dispatch.json");
            DispatchProcessHost.WriteParameters(parametersPath, parameters);

            runTask = Task.Run(() => DispatchProcessHost.Run(parametersPath));
            var childWasObserved = SpinWait.SpinUntil(
                () =>
                {
                    try
                    {
                        if (!File.Exists(heartbeatPath) || !File.Exists(readyPath))
                            return false;

                        using var heartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPath));
                        var childPid = heartbeat.RootElement.GetProperty("childPid");
                        if (childPid.ValueKind != JsonValueKind.Number ||
                            heartbeat.RootElement.GetProperty("ownedPids").GetArrayLength() <= 1)
                        {
                            return false;
                        }

                        if (!int.TryParse(File.ReadAllText(readyPath), out var readyPid) || childPid.GetInt32() != readyPid)
                            return false;
                        heartbeatChildPid = childPid.GetInt32();
                        return true;
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                    catch (JsonException)
                    {
                        return false;
                    }
                },
                TimeSpan.FromSeconds(10));
            Assert.True(childWasObserved, "Dispatch host did not observe the silent child before the fixture timeout.");
            File.WriteAllText(gatePath, "release");
            var rootExitCode = runTask.GetAwaiter().GetResult();

            Assert.Equal(1, rootExitCode);
            AssertNativeExitArtifact(exitPath, 1);
            Assert.Equal(0, new FileInfo(stdoutPath).Length);
            Assert.Equal(0, new FileInfo(stderrPath).Length);
            using (var terminalHeartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPath)))
            {
                Assert.Equal("exited", terminalHeartbeat.RootElement.GetProperty("state").GetString());
                Assert.Contains(
                    terminalHeartbeat.RootElement.GetProperty("ownedProcessIdentities").EnumerateArray(),
                    identity => identity.GetProperty("processId").GetInt32() == heartbeatChildPid);
            }
            using var childExit = JsonDocument.Parse(File.ReadAllText(childExitPath));
            Assert.Equal(heartbeatChildPid, childExit.RootElement.GetProperty("processId").GetInt32());
            Assert.Equal(23, childExit.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("exited", childExit.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            try { File.WriteAllText(gatePath, "release"); } catch { }
            try { runTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void TerminalHeartbeat_ParkedPeriodicWrite_RemainsTerminal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-heartbeat-race-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        Task<int>? runTask = null;
        var periodicWriteTimedOut = 0;
        using var periodicWriteParked = new ManualResetEventSlim();
        using var releasePeriodicWrite = new ManualResetEventSlim();
        var workerGatePath = Path.Combine(dir, "release-worker");
        var heartbeatPath = Path.Combine(dir, "heartbeat.json");
        try
        {
            DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = (destinationPath, state) =>
            {
                if (!string.Equals(destinationPath, heartbeatPath, StringComparison.Ordinal) ||
                    !string.Equals(state, "running", StringComparison.Ordinal))
                {
                    return;
                }

                periodicWriteParked.Set();
                if (!releasePeriodicWrite.Wait(TimeSpan.FromSeconds(10)))
                {
                    Interlocked.Exchange(ref periodicWriteTimedOut, 1);
                }
            };

            var command =
                $"while (!(Test-Path -LiteralPath '{EscapePowerShellSingleQuoted(workerGatePath)}')) " +
                "{ Start-Sleep -Milliseconds 10 }; exit 0";
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                command,
                dir,
                Path.Combine(dir, "out.log"),
                Path.Combine(dir, "err.log"),
                Path.Combine(dir, "exit.txt"),
                heartbeatPath,
                DisableSharedCompilation: false,
                Provider: WorkerSandboxProvider.Claude,
                HeartbeatIntervalMilliseconds: 25);
            var parametersPath = Path.Combine(dir, "dispatch.json");
            DispatchProcessHost.WriteParameters(parametersPath, parameters);

            runTask = Task.Run(() => DispatchProcessHost.Run(parametersPath));
            Assert.True(
                periodicWriteParked.Wait(TimeSpan.FromSeconds(10)),
                "A periodic heartbeat write did not reach the pre-publication hook.");

            File.WriteAllText(workerGatePath, "release");
            Assert.True(
                SpinWait.SpinUntil(() => ReadHeartbeatState(heartbeatPath) == "exited", TimeSpan.FromSeconds(10)),
                "The terminal heartbeat was not published while the periodic write was parked.");

            releasePeriodicWrite.Set();
            Assert.Equal(0, runTask.GetAwaiter().GetResult());
            Assert.Equal(0, Volatile.Read(ref periodicWriteTimedOut));
            Assert.Equal("exited", ReadHeartbeatState(heartbeatPath));
        }
        finally
        {
            DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = null;
            releasePeriodicWrite.Set();
            try { File.WriteAllText(workerGatePath, "release"); } catch { }
            try { runTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void HeartbeatWriteGuard_TerminalPublished_RejectsLateNonTerminalWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-heartbeat-write-guard-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var heartbeatPath = Path.Combine(dir, "heartbeat.json");
            var writer = new DispatchProcessHost.HeartbeatWriteGuard(heartbeatPath, durable: false);

            writer.Write("running", "{\"state\":\"running\"}");
            Assert.Equal("running", ReadHeartbeatState(heartbeatPath));

            writer.WriteTerminal("exited", "{\"state\":\"exited\"}");
            Assert.Equal("exited", ReadHeartbeatState(heartbeatPath));

            writer.Write("running", "{\"state\":\"running\"}");
            Assert.Equal("exited", ReadHeartbeatState(heartbeatPath));
            Assert.Empty(Directory.EnumerateFiles(dir, "heartbeat.json.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_records_failed_stderr_diagnostic_append_in_fallback_artifact")]
    public void DispatchProcessHostRecordsFailedDiagnosticAppend()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-diagnostic-fallback-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var stderrDirectory = Path.Combine(dir, "stderr-is-a-directory");
            Directory.CreateDirectory(stderrDirectory);
            var hostDiagnosticPath = Path.Combine(dir, "host.err.log");
            var heartbeatPath = Path.Combine(dir, "heartbeat.json");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output should-not-launch",
                Path.Combine(dir, "missing-working-directory"),
                Path.Combine(dir, "out.log"),
                stderrDirectory,
                Path.Combine(dir, "exit.txt"),
                heartbeatPath,
                DisableSharedCompilation: false,
                HostDiagnosticPath: hostDiagnosticPath);
            var parametersPath = Path.Combine(dir, "dispatch.json");
            DispatchProcessHost.WriteParameters(parametersPath, parameters);

            var exitCode = DispatchProcessHost.Run(parametersPath);

            Assert.Equal(1, exitCode);
            var fallback = File.ReadAllText(hostDiagnosticPath);
            Assert.Contains("stderr diagnostic append failed", fallback, StringComparison.Ordinal);
            Assert.Contains("original failure", fallback, StringComparison.Ordinal);
            using var heartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPath));
            Assert.Contains(
                "stderr diagnostic append failed",
                heartbeat.RootElement.GetProperty("hostDiagnosticWriteFailure").GetString(),
                StringComparison.Ordinal);
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
            var childLocalAppDataBefore = startInfo.Environment["LOCALAPPDATA"];
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                worktree,
                Path.Combine(root, "out.log"),
                Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"),
                null,
                DisableSharedCompilation: false,
                SandboxLowIntegrity: true,
                WorkerSandboxProvider.Codex);

            DispatchProcessHost.ApplyWorkerSandbox(
                startInfo,
                parameters,
                new WorkerSandboxPreparer(new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true))),
                protectWorkspaceBoundary: _ => { });

            var sandboxBin = Path.Combine(worktree, ".mcg-sandbox", "bin");
            var powershellCache = Path.Combine(worktree, ".mcg-sandbox", "powershell", "ModuleAnalysisCache");
            Assert.True(Directory.Exists(sandboxBin));
            Assert.True(Directory.Exists(Path.GetDirectoryName(powershellCache)!));
            Assert.Equal(powershellCache, startInfo.Environment["PSModuleAnalysisCachePath"]);
            Assert.Equal(childLocalAppDataBefore, startInfo.Environment["LOCALAPPDATA"]);
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

    [Xunit.Fact(DisplayName = "DispatchProcessHost_mandatory_context_preflight_runs_after_integrity_drop_before_provider_command")]
    public void MandatoryContextPreflightRunsAfterIntegrityDropBeforeProviderCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-context-authority-preflight", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var contextPath = Path.Combine(root, "context.md");
            var bytes = Encoding.UTF8.GetBytes("sandbox-visible context");
            File.WriteAllBytes(contextPath, bytes);
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            startInfo.ArgumentList.Add("Write-Output 'provider-command-marker'");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output 'provider-command-marker'",
                root,
                Path.Combine(root, "out.log"),
                Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"),
                null,
                DisableSharedCompilation: false,
                SandboxLowIntegrity: true,
                Provider: WorkerSandboxProvider.Codex,
                MandatoryContextFiles:
                [
                    new MandatoryContextFileDescriptor(
                        "context/required.md",
                        "context.md",
                        WorkerContextArtifact.Hash(bytes),
                        1,
                        AgentRole.Developer,
                        [AgentRole.Developer])
                ]);

            DispatchProcessHost.PrependMandatoryContextAuthorityPreflight(startInfo, parameters);
            if (OperatingSystem.IsWindows())
            {
                DispatchProcessHost.ApplyWorkerSandbox(
                    startInfo,
                    parameters,
                    new WorkerSandboxPreparer(new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true))),
                    protectWorkspaceBoundary: _ => { });
            }

            var command = startInfo.ArgumentList[^1];
            var integrityDrop = command.IndexOf("drop-to-low.ps1", StringComparison.Ordinal);
            var authorityRead = command.IndexOf("$mcgContextManifestPath", StringComparison.Ordinal);
            var providerCommand = command.IndexOf("provider-command-marker", StringComparison.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(integrityDrop >= 0, command);
                Assert.True(authorityRead > integrityDrop, command);
            }
            else
            {
                Assert.True(authorityRead >= 0, command);
            }
            Assert.True(providerCommand > authorityRead, command);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_mandatory_context_manifest_is_git_excluded_on_every_platform")]
    public void MandatoryContextManifestIsGitExcludedOnEveryPlatform()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-context-authority-git-exclude", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(GitCli.Run(root, "init").Succeeded);
            var contextPath = Path.Combine(root, "context.md");
            var bytes = Encoding.UTF8.GetBytes("required context");
            File.WriteAllBytes(contextPath, bytes);
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            startInfo.ArgumentList.Add("Write-Output ok");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                root,
                Path.Combine(root, "out.log"),
                Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"),
                null,
                DisableSharedCompilation: false,
                MandatoryContextFiles:
                [
                    new MandatoryContextFileDescriptor(
                        "context/required.md",
                        "context.md",
                        WorkerContextArtifact.Hash(bytes),
                        ContextContractVersion.V1.Value,
                        AgentRole.Developer,
                        [AgentRole.Developer])
                ]);

            DispatchProcessHost.PrependMandatoryContextAuthorityPreflight(startInfo, parameters);

            var sandboxStatus = GitCli.Run(root, "status", "--short", "--untracked-files=all", "--", ".mcg-sandbox");
            Assert.True(sandboxStatus.Succeeded, sandboxStatus.Error);
            Assert.Equal(string.Empty, sandboxStatus.Output.Trim());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_redirects_PowerShell_cache_when_OS_sandbox_is_disabled")]
    public void ApplyWorkerSandboxRedirectsPowerShellCacheWhenOsSandboxIsDisabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-apply-sandbox-off-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(worktree);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = worktree
            };
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                worktree,
                Path.Combine(root, "out.log"),
                Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"),
                null,
                DisableSharedCompilation: false,
                SandboxLowIntegrity: false);

            var result = DispatchProcessHost.ApplyWorkerSandbox(startInfo, parameters);

            var expectedCache = Path.Combine(worktree, ".mcg-sandbox", "powershell", "ModuleAnalysisCache");
            Assert.False(result.WorktreeRecursiveRelabel);
            Assert.False(result.SandboxRecursiveRelabel);
            Assert.Equal(expectedCache, startInfo.Environment["PSModuleAnalysisCachePath"]);
            Assert.True(Directory.Exists(Path.GetDirectoryName(expectedCache)!));
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

            // Isolated synthetic login source. Claude seeding fails closed on an unusable source, so
            // without an injected source this fixture would resolve the operator's REAL profile
            // credential store and pass or fail according to host auth rather than sandbox scoping.
            var credentialSource = Path.Combine(root, "claude-source");
            Directory.CreateDirectory(credentialSource);
            File.WriteAllText(
                Path.Combine(credentialSource, ".credentials.json"),
                "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-sandbox-scoping-token\"}}");

            var startInfo = CreateSandboxStartInfo(worktree);
            var parameters = CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Claude);

            DispatchProcessHost.ApplyWorkerSandbox(
                startInfo,
                parameters,
                new WorkerSandboxPreparer(new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true))),
                protectWorkspaceBoundary: _ => { },
                providerEnvironmentReader: name => name switch
                {
                    "CLAUDE_CONFIG_DIR" => credentialSource,
                    // Explicitly absent: API-key mode would bypass source seeding entirely, so a host
                    // that happens to export a key must not change what this fixture exercises.
                    "ANTHROPIC_API_KEY" => null,
                    _ => null,
                });

            var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
            Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
            Assert.True(Directory.Exists(Path.Combine(sandboxRoot, "temp")));
            Assert.True(Directory.Exists(Path.Combine(sandboxRoot, "bin")));
            var claudeConfig = Path.Combine(sandboxRoot, "claude-config");
            Assert.Equal(claudeConfig, startInfo.Environment["CLAUDE_CONFIG_DIR"]);

            // The injected source is the one that was seeded: proves the seam is actually honored,
            // so a regression cannot silently fall back to the host store and still pass here.
            Assert.Contains(
                "synthetic-sandbox-scoping-token",
                File.ReadAllText(Path.Combine(claudeConfig, ".credentials.json")),
                StringComparison.Ordinal);

            // The setup artifact reports the resolved source seeding consumed - source kind, directory
            // and status only. It is the same resolved result, so the artifact cannot name one login
            // while the sandbox holds another, and it carries no credential material.
            using var setup = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(sandboxRoot, DispatchProcessHost.LowIntegritySetupArtifactName)));
            var recordedSource = setup.RootElement.GetProperty("credentialSource");
            Assert.Equal(Path.GetFullPath(credentialSource), recordedSource.GetProperty("directory").GetString());
            Assert.True(recordedSource.GetProperty("isExplicitSource").GetBoolean());
            Assert.Equal("LocalMaterialPresent", recordedSource.GetProperty("status").GetString());
            Assert.DoesNotContain(
                "synthetic-sandbox-scoping-token",
                setup.RootElement.GetRawText(),
                StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_seeds_the_transported_selection_over_its_own_environment")]
    public void ApplyWorkerSandboxSeedsTheTransportedSelectionOverItsOwnEnvironment()
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

            // Two usable logins. The conductor selected the first; the dispatch host's own environment
            // points at the second. The host process must seed the conductor's decision - re-selecting
            // locally is exactly how preflight's reported login and the seeded login drifted apart.
            var conductorSource = WriteSyntheticLogin(root, "conductor-source", "transported-selection-token");
            var hostSource = WriteSyntheticLogin(root, "host-source", "host-local-selection-token");

            var preflight = DispatchProcessHost.PreflightClaudeCredentialSource(
                WorkerSandboxProvider.Claude,
                sandboxLowIntegrity: true,
                environmentReader: name => name == "CLAUDE_CONFIG_DIR" ? conductorSource : null);
            var selection = preflight?.ToTransportedSelection();
            Assert.Equal(Path.GetFullPath(conductorSource), selection?.DirectoryPath);

            var startInfo = CreateSandboxStartInfo(worktree);
            var parameters = CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Claude) with
            {
                ClaudeCredentialSelection = selection
            };

            DispatchProcessHost.ApplyWorkerSandbox(
                startInfo,
                parameters,
                new WorkerSandboxPreparer(new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true))),
                protectWorkspaceBoundary: _ => { },
                providerEnvironmentReader: name => name switch
                {
                    "CLAUDE_CONFIG_DIR" => hostSource,
                    // Explicitly absent: API-key mode would bypass source seeding entirely, so a host
                    // that happens to export a key must not change what this fixture exercises.
                    "ANTHROPIC_API_KEY" => null,
                    _ => null,
                });

            var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
            var claudeConfig = Path.Combine(sandboxRoot, "claude-config");
            var seeded = File.ReadAllText(Path.Combine(claudeConfig, ".credentials.json"));
            Assert.Contains("transported-selection-token", seeded, StringComparison.Ordinal);
            Assert.DoesNotContain("host-local-selection-token", seeded, StringComparison.Ordinal);

            // The setup artifact an operator reads names the transported source too, so the reported
            // login and the seeded login are one source of truth.
            using var setup = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(sandboxRoot, DispatchProcessHost.LowIntegritySetupArtifactName)));
            Assert.Equal(
                Path.GetFullPath(conductorSource),
                setup.RootElement.GetProperty("credentialSource").GetProperty("directory").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string WriteSyntheticLogin(string root, string name, string token)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, ".credentials.json"),
            "{\"claudeAiOauth\":{\"accessToken\":\"" + token + "\"}}");
        return directory;
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_scopes_grok_home_to_grok_provider")]
    public void ApplyWorkerSandboxScopesGrokHomeToGrokProvider()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-grok-provider-sandbox-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var userGrok = Path.Combine(root, "user-grok");
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(worktree);
            Directory.CreateDirectory(userGrok);
            File.WriteAllText(Path.Combine(userGrok, "auth.json"), "{\"token\":\"test\"}\n");
            File.WriteAllText(Path.Combine(userGrok, "config.toml"), "[cli]\nauto_update = false\n");

            var startInfo = CreateSandboxStartInfo(worktree);
            DispatchProcessHost.SeedGrokEnvironment(
                startInfo,
                Path.Combine(worktree, ".mcg-sandbox"),
                grokHomeDirectoryAccessor: () => userGrok);

            var grokHome = Path.Combine(worktree, ".mcg-sandbox", "grok-home");
            Assert.Equal(grokHome, startInfo.Environment["GROK_HOME"]);
            Assert.Equal("1", startInfo.Environment["GROK_DISABLE_AUTOUPDATER"]);
            Assert.True(File.Exists(Path.Combine(grokHome, "auth.json")));
            Assert.True(File.Exists(Path.Combine(grokHome, "config.toml")));
            Assert.Contains("token", File.ReadAllText(Path.Combine(grokHome, "auth.json")), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ApplyWorkerSandbox_sets_grok_home_for_grok_provider")]
    public void ApplyWorkerSandboxSetsGrokHomeForGrokProvider()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-grok-apply-sandbox-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(worktree);
            var startInfo = CreateSandboxStartInfo(worktree);
            var parameters = CreateSandboxParameters(root, worktree, WorkerSandboxProvider.Grok);

            DispatchProcessHost.ApplyWorkerSandbox(
                startInfo,
                parameters,
                new WorkerSandboxPreparer(new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true))),
                protectWorkspaceBoundary: _ => { });

            var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
            var grokHome = Path.Combine(sandboxRoot, "grok-home");
            Assert.Equal(grokHome, startInfo.Environment["GROK_HOME"]);
            Assert.True(Directory.Exists(grokHome));
            Assert.Equal(
                worktree.Replace('\\', '/'),
                startInfo.Environment[HarnessHookRootContract.EnvironmentVariableName]);
            Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
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
                new WorkerSandboxPreparer(new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true))),
                protectWorkspaceBoundary: _ => { });

            var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
            Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
            Assert.False(startInfo.Environment.ContainsKey("GROK_HOME"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "claude-config")));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "grok-home")));
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

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_read_only_preparation_labels_only_sandbox_root")]
    public void WorkerSandboxPreparerReadOnlyPreparationLabelsOnlySandboxRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-sandbox-read-only-test", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(root, "worktree");
        var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
        var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: false, Low: false, Inheritable: false));
        try
        {
            var result = new WorkerSandboxPreparer(labeler).PrepareSandboxRootOnly(worktree, sandboxRoot);

            var setCall = Assert.Single(labeler.SetCalls);
            Assert.Equal(sandboxRoot, setCall.Path);
            Assert.False(setCall.Recursive);
            Assert.False(result.WorktreeRecursiveRelabel);
            Assert.True(File.Exists(Path.Combine(sandboxRoot, WorkerSandboxPreparer.MarkerFileName)));
            Assert.False(File.Exists(Path.Combine(worktree, WorkerSandboxPreparer.MarkerFileName)));
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

            Assert.False(result.PrepReceiptHit);
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

            Assert.False(result.PrepReceiptHit);
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
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
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
                DisableSharedCompilation: false,
                OutputDrainPolicy: new ProcessOutputDrainPolicy(
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(200))));

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

            // The exit file must appear within the injected drain policy plus host startup.
            // The 30s poll is a hang failsafe only; do not assert wall-clock.
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
            AssertNativeExitArtifact(exitCodePath, 0);
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

    [Xunit.Fact]
    public void DispatchProcessIdentityEvidence_CaptureDropsIdentityWhenOwnershipChangesDuringCapture()
    {
        const int processId = 43_316;
        var identity = new SpawnProcessIdentity(
            processId,
            DateTimeOffset.Parse("2026-08-24T14:20:35Z"),
            @"C:\workers\claude.exe");
        var identityCaptured = false;

        var captured = DispatchProcessIdentityEvidence.Capture(
            [processId],
            readCurrentOwnedProcessIds: () =>
            {
                Assert.True(identityCaptured, "Ownership must be revalidated after the bare PID is reopened.");
                return [];
            },
            readCurrentIdentity: _ =>
            {
                identityCaptured = true;
                return identity;
            });

        Assert.Empty(captured);
    }

    [Xunit.Fact]
    public void DispatchProcessIdentityEvidence_CaptureRetainsIdentityWhileProcessRemainsOwned()
    {
        const int processId = 43_316;
        var identity = new SpawnProcessIdentity(
            processId,
            DateTimeOffset.Parse("2026-08-24T14:20:35Z"),
            @"C:\workers\claude.exe");

        var captured = DispatchProcessIdentityEvidence.Capture(
            [processId],
            readCurrentOwnedProcessIds: () => [processId],
            readCurrentIdentity: _ => identity);

        Assert.Equal([identity], captured);
    }

    [Xunit.Fact]
    public void DispatchProcessIdentityEvidence_MissingOrRecycledIdentityNeverEstablishesOwnership()
    {
        const int processId = 43_316;
        var recorded = new SpawnProcessIdentity(
            processId,
            DateTimeOffset.Parse("2026-08-24T14:20:35Z"),
            @"C:\workers\claude.exe");
        var recycled = recorded with { StartedAt = recorded.StartedAt.AddDays(3) };

        Assert.False(DispatchProcessIdentityEvidence.IsRecordedOwner(processId, [], _ => recycled));
        Assert.False(DispatchProcessIdentityEvidence.IsRecordedOwner(processId, [recorded], _ => recycled));
        Assert.False(DispatchProcessIdentityEvidence.IsRecordedOwner(processId, [recorded], _ => null));
        Assert.True(DispatchProcessIdentityEvidence.IsRecordedOwner(processId, [recorded], _ => recorded));
    }

    [Xunit.Fact]
    public void DispatchHeartbeatIdentityTracker_RecordsRecycledCurrentOwnerWithoutErasingHistory()
    {
        const int hostProcessId = 10;
        const int workerProcessId = 20;
        var host = new SpawnProcessIdentity(hostProcessId, DateTimeOffset.Parse("2026-08-30T05:48:32Z"), @"C:\host.exe");
        var worker = new SpawnProcessIdentity(workerProcessId, DateTimeOffset.Parse("2026-08-30T05:48:33Z"), @"C:\worker.exe");
        var recycled = worker with { StartedAt = worker.StartedAt.AddMinutes(30) };
        var tracker = new DispatchHeartbeatProcessIdentityTracker();

        var first = tracker.Capture(
            hostProcessId,
            [workerProcessId],
            () => [workerProcessId],
            processId => processId == hostProcessId ? host : worker);
        var second = tracker.Capture(
            hostProcessId,
            [workerProcessId],
            () => [workerProcessId],
            processId => processId == hostProcessId ? host : recycled);

        Assert.Contains(worker, first.Current);
        Assert.Contains(recycled, second.Current);
        Assert.Contains(worker, second.Recorded);
        Assert.Contains(recycled, second.Recorded);
        Assert.All(second.Current, identity => Assert.Contains(identity, second.Recorded));
    }

    [Xunit.Fact]
    public void DispatchProcessHost_CaptureHeartbeatProcessIdentitiesIncludesHostIdentity()
    {
        var hostIdentity = new SpawnProcessIdentity(
            43_316,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            @"C:\tools\dispatch-host.exe");
        var workerIdentity = new SpawnProcessIdentity(
            26_084,
            DateTimeOffset.UtcNow.AddMinutes(-9),
            @"C:\tools\worker.exe");

        var captured = DispatchProcessHost.CaptureHeartbeatProcessIdentities(
            hostIdentity.ProcessId,
            [workerIdentity.ProcessId],
            () => [workerIdentity.ProcessId],
            processId => processId switch
            {
                43_316 => hostIdentity,
                26_084 => workerIdentity,
                _ => null
            });

        Assert.Contains(hostIdentity, captured);
        Assert.Contains(workerIdentity, captured);
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_heartbeat_cpu_and_pids_reflect_wrapped_grandchild")]
    public void DispatchProcessHostHeartbeatCpuAndPidsReflectWrappedGrandchild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-grandchild-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var childScriptPath = Path.Combine(dir, "grandchild-reap-probe.ps1");
        var childPidPath = Path.Combine(dir, "child.pid");
        var startMarkerPath = Path.Combine(dir, "start.marker");
        var launchFailurePath = Path.Combine(dir, "launch-failure.txt");
        var waitFailurePath = Path.Combine(dir, "wait-failure.txt");
        var probeStdoutPath = Path.Combine(dir, "grandchild-reap-probe.stdout.log");
        var probeStderrPath = Path.Combine(dir, "grandchild-reap-probe.stderr.log");
        var probeIdentityPath = Path.Combine(dir, "grandchild-reap-probe.log");
        var fixtureTimeout = GetGrandchildReapFixtureTimeout();
        File.WriteAllText(
            childScriptPath,
            """
            $identity = 'Dispatch-host process-tree-reaping test fixture; safe to terminate.'
            try { $Host.UI.RawUI.WindowTitle = $identity } catch { }
            Add-Content -LiteralPath (Join-Path $PSScriptRoot 'grandchild-reap-probe.log') -Value $identity
            try { Write-Output $identity } catch { }
            $deadline = [DateTime]::UtcNow.AddSeconds(6)
            while ([DateTime]::UtcNow -lt $deadline) {
                for ($i = 0; $i -lt 50000; $i++) {
                    [Math]::Sqrt($i) > $null
                }
            }
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Process? wrapper = null;
        OwnedProcessGroup? group = null;
        int? childPid = null;
        ConductorSupervisorProcessIdentity? childIdentity = null;
        try
        {
            var startInfo = BuildGrandchildReapWrapperStartInfo(
                dir,
                childScriptPath,
                childPidPath,
                startMarkerPath,
                launchFailurePath,
                waitFailurePath,
                probeStdoutPath,
                probeStderrPath,
                WorkerShell.Executable,
                fixtureTimeout);
            wrapper = StartGrandchildReapWrapper(startInfo, ProcessTreeGuiSuppression.Start);

            group = OwnedProcessGroup.Attach(wrapper);
            File.WriteAllText(startMarkerPath, "go");
            childPid = WaitForGrandchildLaunch(
                childPidPath,
                launchFailurePath,
                waitFailurePath,
                wrapper,
                fixtureTimeout);
            childIdentity = TestOwnedProcessStop.TryIdentify(childPid.Value);
            Assert.NotEqual(wrapper.Id, childPid.Value);

            using var child = Process.GetProcessById(childPid.Value);
            child.Refresh();
            Assert.False(child.HasExited, $"Grandchild reap probe exited before its window state could be checked. pid={childPid}");
            Assert.Equal(IntPtr.Zero, child.MainWindowHandle);
            var probeIdentity = string.Empty;
            Assert.True(
                WaitUntil(() => TryReadFixtureArtifact(probeIdentityPath, out probeIdentity), fixtureTimeout),
                $"Grandchild identity marker timed out after {fixtureTimeout.TotalSeconds:0} seconds: '{probeIdentityPath}'.");
            Assert.Contains(
                "Dispatch-host process-tree-reaping test fixture; safe to terminate.",
                probeIdentity,
                StringComparison.Ordinal);

            IReadOnlyList<int> lastOwnedPids = [];
            long lastOwnedCpuMs = 0;
            int? lastSelectedChildPid = null;
            var sawGrandchildCpu = WaitUntil(() =>
            {
                var ownedPids = DispatchProcessHost.GetHeartbeatOwnedProcessIds(group, wrapper);
                var ownedCpuMs = DispatchProcessHost.ReadHeartbeatOwnedCpuMs(group, ownedPids);
                lastOwnedPids = ownedPids;
                lastOwnedCpuMs = ownedCpuMs;
                lastSelectedChildPid = DispatchProcessHost.SelectHeartbeatChildPid(wrapper, ownedPids);
                return ownedCpuMs > 100 &&
                    ownedPids.Contains(wrapper.Id) &&
                    ownedPids.Contains(childPid.Value) &&
                    lastSelectedChildPid == childPid.Value;
            }, TimeSpan.FromSeconds(5));

            Assert.True(
                sawGrandchildCpu,
                $"Expected live job accounting and heartbeat PIDs to include the CPU-burning grandchild. wrapper={wrapper.Id} child={childPid} selected={lastSelectedChildPid?.ToString() ?? "null"} cpuMs={lastOwnedCpuMs} ownedPids=[{string.Join(",", lastOwnedPids)}]");

            wrapper.Kill(entireProcessTree: true);
            Assert.True(
                child.WaitForExit((int)fixtureTimeout.TotalMilliseconds),
                $"Grandchild reap after wrapper termination timed out after {fixtureTimeout.TotalSeconds:0} seconds. wrapper={wrapper.Id} child={childPid}");
        }
        finally
        {
            CleanupGrandchildReapFixture(dir, childIdentity, group, wrapper, fixtureTimeout);
        }
    }

    [Xunit.Fact]
    public void GrandchildSpawnUsesGuiSuppression()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-grandchild-tests", Guid.NewGuid().ToString("n"));
        var startInfo = BuildGrandchildReapWrapperStartInfo(
            fixtureRoot,
            Path.Combine(fixtureRoot, "grandchild-reap-probe.ps1"),
            Path.Combine(fixtureRoot, "child.pid"),
            Path.Combine(fixtureRoot, "start.marker"),
            Path.Combine(fixtureRoot, "launch-failure.txt"),
            Path.Combine(fixtureRoot, "wait-failure.txt"),
            Path.Combine(fixtureRoot, "probe.stdout.log"),
            Path.Combine(fixtureRoot, "probe.stderr.log"),
            WorkerShell.Executable,
            GetGrandchildReapFixtureTimeout());
        Assert.Contains(
            startInfo.ArgumentList,
            argument => argument.Contains("Start-Process", StringComparison.Ordinal) &&
                argument.Contains("-NoNewWindow", StringComparison.Ordinal));
        var invoked = false;

        var exception = Assert.Throws<GrandchildFixtureStartSentinel>(() =>
            StartGrandchildReapWrapper(startInfo, request =>
            {
                invoked = true;
                Assert.Same(startInfo, request);
                Assert.False(request.UseShellExecute);
                throw new GrandchildFixtureStartSentinel();
            }));

        Assert.NotNull(exception);
        Assert.True(invoked, "The fixture did not route its wrapper spawn through the console-suppression seam.");
    }

    [Xunit.Fact]
    public void GrandchildLaunchFailureReportsCommand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-grandchild-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var missingExecutable = Path.Combine(dir, "missing-pwsh.exe");
        var childScriptPath = Path.Combine(dir, "grandchild-reap-probe.ps1");
        var childPidPath = Path.Combine(dir, "child.pid");
        var startMarkerPath = Path.Combine(dir, "start.marker");
        var launchFailurePath = Path.Combine(dir, "launch-failure.txt");
        var waitFailurePath = Path.Combine(dir, "wait-failure.txt");
        var fixtureTimeout = GetGrandchildReapFixtureTimeout();
        Process? wrapper = null;
        OwnedProcessGroup? group = null;
        try
        {
            File.WriteAllText(childScriptPath, "exit 0", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var startInfo = BuildGrandchildReapWrapperStartInfo(
                dir,
                childScriptPath,
                childPidPath,
                startMarkerPath,
                launchFailurePath,
                waitFailurePath,
                Path.Combine(dir, "probe.stdout.log"),
                Path.Combine(dir, "probe.stderr.log"),
                missingExecutable,
                fixtureTimeout);
            wrapper = StartGrandchildReapWrapper(startInfo, ProcessTreeGuiSuppression.Start);
            group = OwnedProcessGroup.Attach(wrapper);
            File.WriteAllText(startMarkerPath, "go");

            var failure = Assert.Throws<InvalidOperationException>(() =>
                WaitForGrandchildLaunch(
                    childPidPath,
                    launchFailurePath,
                    waitFailurePath,
                    wrapper,
                    fixtureTimeout));

            Assert.Contains("Grandchild launch failed", failure.Message, StringComparison.Ordinal);
            Assert.Contains("Win32 error 0x00000002", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(missingExecutable, failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(childScriptPath, failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupGrandchildReapFixture(dir, null, group, wrapper, fixtureTimeout);
        }
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

    [Xunit.Fact(DisplayName = "DispatchProcessHost_receipt_hit_prep_stays_fast_across_repeated_dispatches")]
    public void DispatchProcessHostReceiptHitPrepStaysFastAcrossRepeatedDispatches()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (GetCurrentProcessIntegrityRid() < MediumIntegrityRid)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-receipt-hit-measurement", Guid.NewGuid().ToString("n"));
        var repo = Path.Combine(root, "repo");
        var worktree = Path.Combine(root, "linked-worktree");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            CreateLinkedWorktree(repo, worktree);
            MeasuredDispatch? warmup = null;
            var receiptHitMeasurements = new List<MeasuredDispatch>();
            var artifactWriteAttempted = false;
            Exception? artifactWriteFailure = null;

            void WriteMeasurementArtifactBestEffort()
            {
                if (warmup is null || artifactWriteAttempted)
                {
                    return;
                }

                artifactWriteAttempted = true;
                try
                {
                    var measurementArtifact = WriteReceiptHitMeasurementArtifact(root, warmup, receiptHitMeasurements);
                    Console.WriteLine($"receipt-hit measurement artifact: {measurementArtifact}");
                }
                catch (Exception exception)
                {
                    artifactWriteFailure = exception;
                }
            }

            try
            {
                warmup = RunMeasuredDispatch(root, worktree, logs, "warmup");
                Assert.Equal("complete", warmup.Phase);
                Assert.False(warmup.PrepReceiptHit);
                Assert.Contains(
                    WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase,
                    warmup.SandboxPrepEvents.Select(evt => evt.Phase));
                Assert.Contains(
                    WorkerSandboxPreparer.ProtectGitMetadataPhase,
                    warmup.SandboxPrepEvents.Select(evt => evt.Phase));

                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    var measurement = RunMeasuredDispatch(root, worktree, logs, $"receipt-hit-{attempt}");
                    receiptHitMeasurements.Add(measurement);
                }

                WriteMeasurementArtifactBestEffort();

                Assert.Equal(2, receiptHitMeasurements.Count);
                Assert.Equal(
                    ["receipt-hit-1.dispatch.json", "receipt-hit-2.dispatch.json", "warmup.dispatch.json"],
                    Directory.GetFiles(logs, "*.dispatch.json").Select(Path.GetFileName).OrderBy(name => name));

                Assert.All(receiptHitMeasurements, measurement =>
                {
                    var phases = measurement.SandboxPrepEvents.Select(evt => evt.Phase).ToArray();
                    Assert.DoesNotContain(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, phases);
                    Assert.DoesNotContain(WorkerSandboxPreparer.ProtectGitMetadataPhase, phases);
                    Assert.Equal("receipt-hit", measurement.Phase);
                    Assert.Contains("receipt-fast-path", phases);
                    Assert.True(measurement.PrepReceiptHit);
                    Assert.Contains("worker-first-output", File.ReadAllText(measurement.StdoutPath), StringComparison.Ordinal);
                });

                if (artifactWriteFailure is not null)
                {
                    throw new InvalidOperationException("Failed to write the receipt-hit measurement artifact.", artifactWriteFailure);
                }
            }
            catch
            {
                WriteMeasurementArtifactBestEffort();
                throw;
            }
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
            DisableSharedCompilation: false,
            SandboxLowIntegrity: true,
            provider);
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        if (result.TimedOut)
            throw new TimeoutException($"git {string.Join(' ', arguments)} timed out: {result}");
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit {result.ExitCode}: {result.StandardError}; {result}");
        }

        return result.StandardOutput.Trim();
    }

    internal static string WriteReceiptHitMeasurementArtifact(
        string ownedRoot,
        MeasuredDispatch warmup,
        IReadOnlyCollection<MeasuredDispatch> receiptHitMeasurements)
    {
        var artifactPath = Path.Combine(ownedRoot, "receipt-hit-measurement.json");

        var artifact = new
        {
            measurementModel = "DispatchElapsedMs includes SandboxPrepElapsedMs; dispatchMinusSandboxPrepElapsedMs is the exclusive remainder.",
            warmup = new
            {
                phase = warmup.Phase,
                sandboxPrepElapsedMs = warmup.SandboxPrepElapsedMs,
                dispatchElapsedMs = warmup.DispatchElapsedMs,
                dispatchMinusSandboxPrepElapsedMs = warmup.DispatchElapsedMs - warmup.SandboxPrepElapsedMs,
                sandboxPrepEvents = SerializeSandboxPrepEvents(warmup.SandboxPrepEvents)
            },
            receiptHits = receiptHitMeasurements.Select(measurement => new
            {
                phase = measurement.Phase,
                sandboxPrepElapsedMs = measurement.SandboxPrepElapsedMs,
                dispatchElapsedMs = measurement.DispatchElapsedMs,
                dispatchMinusSandboxPrepElapsedMs = measurement.DispatchElapsedMs - measurement.SandboxPrepElapsedMs,
                sandboxPrepEvents = SerializeSandboxPrepEvents(measurement.SandboxPrepEvents)
            }).ToArray()
        };

        File.WriteAllText(
            artifactPath,
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return artifactPath;
    }

    private static MeasuredDispatch RunMeasuredDispatch(string root, string worktree, string logs, string label)
    {
        var stdoutPath = Path.Combine(logs, $"{label}.out.log");
        var stderrPath = Path.Combine(logs, $"{label}.err.log");
        var exitPath = Path.Combine(logs, $"{label}.exit.txt");
        var heartbeatPath = Path.Combine(logs, $"{label}.heartbeat.json");
        var parametersPath = Path.Combine(logs, $"{label}.dispatch.json");
        var prepRecordPath = Path.Combine(logs, $"{label}.prep.json");
        var prepHeartbeatPath = Path.Combine(logs, $"{label}.prep.heartbeat.json");
        var prepExitPath = Path.Combine(logs, $"{label}.prep.exit.txt");
        var markerPath = Path.Combine(worktree, $"{label}.worker-started.txt");
        var command =
            $"Set-Content -LiteralPath '{EscapePowerShellSingleQuoted(markerPath)}' -Value 'worker-first-output'; " +
            "Write-Output worker-first-output";
        DispatchProcessHost.WritePrepRecord(prepRecordPath, new DispatchProcessHost.DispatchPrepRecord(
            DispatchProcessHost.PrepDispatchKind,
            "goal-prep-record",
            "task-prep-record",
            worktree,
            prepHeartbeatPath,
            prepExitPath,
            DateTimeOffset.UtcNow,
            WorkerSandboxProvider.Codex,
            SandboxWorktreeWritable: true,
            CompletedAt: null));
        DispatchProcessHost.WriteParameters(parametersPath, new DispatchProcessHost.DispatchRunParameters(
            command,
            worktree,
            stdoutPath,
            stderrPath,
            exitPath,
            heartbeatPath,
            DisableSharedCompilation: false,
            SandboxLowIntegrity: true,
            WorkerSandboxProvider.Codex,
            Kind: DispatchProcessHost.WorkerDispatchKind,
            PrepGoalId: "goal-prep-record",
            PrepTaskId: "task-prep-record",
            PrepRecordPath: prepRecordPath,
            PrepHeartbeatPath: prepHeartbeatPath,
            PrepExitCodePath: prepExitPath));

        var stopwatch = Stopwatch.StartNew();
        var exitCode = DispatchProcessHost.Run(parametersPath);
        stopwatch.Stop();

        Assert.Equal(0, exitCode);
        AssertNativeExitArtifact(exitPath, 0);
        Assert.Equal("0", File.ReadAllText(prepExitPath).Trim());
        Assert.True(File.Exists(markerPath), File.ReadAllText(stderrPath));
        using (var workerHeartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPath)))
        {
            Assert.Equal(DispatchProcessHost.WorkerDispatchKind, workerHeartbeat.RootElement.GetProperty("kind").GetString());
        }

        using (var prepRecord = JsonDocument.Parse(File.ReadAllText(prepRecordPath)))
        {
            Assert.Equal(DispatchProcessHost.PrepDispatchKind, prepRecord.RootElement.GetProperty("kind").GetString());
            Assert.Equal("goal-prep-record", prepRecord.RootElement.GetProperty("goalId").GetString());
            Assert.Equal("task-prep-record", prepRecord.RootElement.GetProperty("taskId").GetString());
            Assert.Equal(prepHeartbeatPath, prepRecord.RootElement.GetProperty("heartbeatPath").GetString());
            Assert.Equal(prepExitPath, prepRecord.RootElement.GetProperty("exitCodePath").GetString());
        }

        using (var prepHeartbeat = JsonDocument.Parse(File.ReadAllText(prepHeartbeatPath)))
        {
            Assert.Equal(DispatchProcessHost.PrepDispatchKind, prepHeartbeat.RootElement.GetProperty("kind").GetString());
            Assert.NotEqual(heartbeatPath, prepHeartbeatPath);
        }

        var sandboxPrepEvents = ReadSandboxPrepEvents(stderrPath);
        var setupArtifactPath = Path.Combine(
            worktree,
            ".mcg-sandbox",
            DispatchProcessHost.LowIntegritySetupArtifactName);
        using var setupArtifact = JsonDocument.Parse(File.ReadAllText(setupArtifactPath));
        var prepReceiptHit = setupArtifact.RootElement.GetProperty("prepReceiptHit").GetBoolean();
        var terminalPrepEvent = sandboxPrepEvents
            .LastOrDefault(evt =>
            {
                var phase = evt.GetProperty("phase").GetString();
                return phase is "complete" or "receipt-hit";
            });
        Assert.NotEqual(default, terminalPrepEvent.ValueKind);
        Assert.True(terminalPrepEvent.TryGetProperty("elapsedMs", out var elapsed), File.ReadAllText(stderrPath));

        return new MeasuredDispatch(
            terminalPrepEvent.GetProperty("phase").GetString() ?? string.Empty,
            elapsed.GetInt64(),
            stopwatch.ElapsedMilliseconds,
            prepReceiptHit,
            stdoutPath,
            sandboxPrepEvents.Select(ToSandboxPrepEvent).ToArray());
    }

    private static JsonElement[] ReadSandboxPrepEvents(string stderrPath)
    {
        return File.ReadAllLines(stderrPath)
            .Where(line => line.Contains("\"event\":\"sandbox-prep\"", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
    }

    private static object[] SerializeSandboxPrepEvents(IReadOnlyCollection<SandboxPrepEvent> events)
        => events.Select(evt => new
        {
            phase = evt.Phase,
            startedAt = evt.StartedAt,
            elapsedMs = evt.ElapsedMs
        }).Cast<object>().ToArray();

    private static SandboxPrepEvent ToSandboxPrepEvent(JsonElement evt)
        => new(
            evt.GetProperty("phase").GetString() ?? string.Empty,
            evt.GetProperty("startedAt").GetDateTimeOffset(),
            evt.TryGetProperty("elapsedMs", out var elapsedMs) ? elapsedMs.GetInt64() : null);

    private static ProcessStartInfo BuildGrandchildReapWrapperStartInfo(
        string fixtureRoot,
        string childScriptPath,
        string childPidPath,
        string startMarkerPath,
        string launchFailurePath,
        string waitFailurePath,
        string probeStdoutPath,
        string probeStderrPath,
        string childExecutable,
        TimeSpan timeout)
    {
        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        var commandLine = FormatGrandchildCommandLine(childExecutable, childScriptPath);
        var command =
            "$startDeadline = [DateTime]::UtcNow.AddSeconds([int]$env:MCG_GRANDCHILD_TIMEOUT_SECONDS); " +
            "while (!(Test-Path -LiteralPath $env:MCG_GRANDCHILD_START_MARKER)) { " +
            "if ([DateTime]::UtcNow -ge $startDeadline) { " +
            "Set-Content -LiteralPath $env:MCG_GRANDCHILD_WAIT_FAILURE -Value 'Grandchild start gate timed out'; exit 124 }; " +
            "Start-Sleep -Milliseconds 25 }; " +
            "try { " +
            "$child = Start-Process -FilePath $env:MCG_GRANDCHILD_EXECUTABLE " +
            "-ArgumentList @('-NoProfile','-NonInteractive','-InputFormat','None','-ExecutionPolicy','Bypass','-File',$env:MCG_GRANDCHILD_SCRIPT) " +
            "-RedirectStandardOutput $env:MCG_GRANDCHILD_STDOUT -RedirectStandardError $env:MCG_GRANDCHILD_STDERR " +
            "-NoNewWindow -PassThru -ErrorAction Stop " +
            "} catch { " +
            "$exception = $_.Exception; while ($exception -and -not ($exception -is [System.ComponentModel.Win32Exception])) { $exception = $exception.InnerException }; " +
            "$nativeCode = if ($exception) { $exception.NativeErrorCode } elseif (!(Test-Path -LiteralPath $env:MCG_GRANDCHILD_EXECUTABLE -PathType Leaf)) { 2 } else { $_.Exception.HResult -band 0xffff }; " +
            "$hresult = $_.Exception.HResult -band 0xffffffffL; " +
            "$symbol = if ($nativeCode -eq 232) { ' / ERROR_NO_DATA' } else { '' }; " +
            "$message = 'Grandchild launch failed: Win32 error 0x{0:x8}{1}; HRESULT 0x{2:x8}; command: {3}; detail: {4}' -f $nativeCode,$symbol,$hresult,$env:MCG_GRANDCHILD_COMMAND_LINE,$_.Exception.Message; " +
            "Set-Content -LiteralPath $env:MCG_GRANDCHILD_LAUNCH_FAILURE -Value $message; exit 125 }; " +
            "Set-Content -LiteralPath $env:MCG_GRANDCHILD_PID -Value $child.Id; " +
            "try { Wait-Process -Id $child.Id -Timeout ([int]$env:MCG_GRANDCHILD_TIMEOUT_SECONDS) -ErrorAction Stop } catch { " +
            "$message = 'Grandchild reap probe exit timed out: pid={0}; command: {1}' -f $child.Id,$env:MCG_GRANDCHILD_COMMAND_LINE; " +
            "Set-Content -LiteralPath $env:MCG_GRANDCHILD_WAIT_FAILURE -Value $message; exit 124 }";
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = fixtureRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments(WorkerShell.BaseArguments().Concat([command]));
        startInfo.Environment["MCG_GRANDCHILD_EXECUTABLE"] = childExecutable;
        startInfo.Environment["MCG_GRANDCHILD_SCRIPT"] = childScriptPath;
        startInfo.Environment["MCG_GRANDCHILD_PID"] = childPidPath;
        startInfo.Environment["MCG_GRANDCHILD_START_MARKER"] = startMarkerPath;
        startInfo.Environment["MCG_GRANDCHILD_LAUNCH_FAILURE"] = launchFailurePath;
        startInfo.Environment["MCG_GRANDCHILD_WAIT_FAILURE"] = waitFailurePath;
        startInfo.Environment["MCG_GRANDCHILD_STDOUT"] = probeStdoutPath;
        startInfo.Environment["MCG_GRANDCHILD_STDERR"] = probeStderrPath;
        startInfo.Environment["MCG_GRANDCHILD_COMMAND_LINE"] = commandLine;
        startInfo.Environment["MCG_GRANDCHILD_TIMEOUT_SECONDS"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return startInfo;
    }

    private static Process StartGrandchildReapWrapper(
        ProcessStartInfo startInfo,
        Func<ProcessStartInfo, Process> startWithConsoleSuppression)
    {
        ArgumentNullException.ThrowIfNull(startWithConsoleSuppression);
        return startWithConsoleSuppression(startInfo);
    }

    private static int WaitForGrandchildLaunch(
        string childPidPath,
        string launchFailurePath,
        string waitFailurePath,
        Process wrapper,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (TryReadFixtureArtifact(launchFailurePath, out var launchFailure))
            {
                throw new InvalidOperationException(launchFailure);
            }

            if (TryReadFixtureArtifact(waitFailurePath, out var waitFailure))
            {
                throw new TimeoutException(waitFailure);
            }

            if (TryReadFixtureArtifact(childPidPath, out var childPidText) &&
                int.TryParse(childPidText, out var processId) &&
                processId > 0)
            {
                return processId;
            }

            if (wrapper.HasExited)
            {
                throw new InvalidOperationException(
                    $"Grandchild reap wrapper exited with code {wrapper.ExitCode} before publishing a pid or named launch failure.");
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            $"Grandchild launch result timed out after {timeout.TotalSeconds:0} seconds while waiting for pid file '{childPidPath}' or failure file '{launchFailurePath}'.");
    }

    private static string FormatGrandchildCommandLine(string executable, string scriptPath) =>
        $"\"{executable}\" -NoProfile -NonInteractive -InputFormat None -ExecutionPolicy Bypass -File \"{scriptPath}\"";

    private static TimeSpan GetGrandchildReapFixtureTimeout()
    {
        const int defaultTimeoutSeconds = 30;
        var configured = Environment.GetEnvironmentVariable("MCG_GRANDCHILD_REAP_FIXTURE_TIMEOUT_SECONDS");
        return int.TryParse(configured, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(defaultTimeoutSeconds);
    }

    private static bool TryReadFixtureArtifact(string path, out string content)
    {
        content = string.Empty;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var published = File.ReadAllText(path);
            if (!published.EndsWith('\n'))
            {
                return false;
            }

            content = published.Trim();
            return content.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CleanupGrandchildReapFixture(
        string fixtureRoot,
        ConductorSupervisorProcessIdentity? childIdentity,
        OwnedProcessGroup? group,
        Process? wrapper,
        TimeSpan timeout)
    {
        TestOwnedProcessStop.StopTreeIfSame(childIdentity);

        try { group?.Kill(); } catch { }
        try { wrapper?.Kill(entireProcessTree: true); } catch { }
        try { wrapper?.Dispose(); } catch { }

        var fixtureProcessIds = FindFixturePwshProcessIds(fixtureRoot);
        foreach (var fixtureProcessId in fixtureProcessIds)
        {
            TestOwnedProcessStop.StopTreeIfSame(fixtureProcessId);
        }

        var reaped = WaitUntil(
            () => fixtureProcessIds.All(identity => TestOwnedProcessStop.TryIdentify(identity.ProcessId) != identity),
            timeout);
        var lateFixtureProcessIds = FindFixturePwshProcessIds(fixtureRoot);
        foreach (var fixtureProcessId in lateFixtureProcessIds)
        {
            TestOwnedProcessStop.StopTreeIfSame(fixtureProcessId);
        }

        var lateReaped = WaitUntil(
            () => lateFixtureProcessIds.All(identity => TestOwnedProcessStop.TryIdentify(identity.ProcessId) != identity),
            timeout);
        var survivingFixtureProcessIds = FindFixturePwshProcessIds(fixtureRoot);
        try { Directory.Delete(fixtureRoot, recursive: true); } catch { }
        Assert.True(
            reaped && lateReaped && survivingFixtureProcessIds.Count == 0,
            $"Grandchild fixture teardown timed out after {timeout.TotalSeconds:0} seconds; pwsh processes still reference fixture root '{fixtureRoot}': [{string.Join(",", survivingFixtureProcessIds)}]");
    }

    private static IReadOnlyList<ConductorSupervisorProcessIdentity> FindFixturePwshProcessIds(string fixtureRoot)
    {
        var processName = Path.GetFileNameWithoutExtension(WorkerShell.Executable);
        var processes = Process.GetProcessesByName(processName);

        try
        {
            var identities = processes.Select(TestOwnedProcessStop.Identify)
                .OfType<ConductorSupervisorProcessIdentity>().ToArray();
            var processIds = identities.Select(identity => identity.ProcessId).ToArray();
            if (processIds.Length == 0)
            {
                return [];
            }

            var commandLines = ProcessCommandLines.Read(processIds);
            var matchedIds = commandLines
                .Where(pair => pair.Value.Contains(fixtureRoot, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key).ToHashSet();
            return identities.Where(identity => matchedIds.Contains(identity.ProcessId)).ToArray();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private sealed class GrandchildFixtureStartSentinel : Exception
    {
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    private static int WaitForPidFile(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path) &&
                int.TryParse(File.ReadAllText(path).Trim(), out var processId) &&
                processId > 0)
            {
                return processId;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"Timed out waiting for pid file '{path}'.");
    }

    internal sealed record MeasuredDispatch(
        string Phase,
        long SandboxPrepElapsedMs,
        long DispatchElapsedMs,
        bool PrepReceiptHit,
        string StdoutPath,
        IReadOnlyCollection<SandboxPrepEvent> SandboxPrepEvents);

    internal sealed record SandboxPrepEvent(string Phase, DateTimeOffset StartedAt, long? ElapsedMs);

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
        // Receipt-hit verification queries the roots whose receipt/protection state can invalidate
        // the fast path. In subscription workers, temp directories can sit below the repository root,
        // so git common-dir discovery may also query the outer .git directory.
        Assert.Contains(worktree, labeler.QueryCalls);
        Assert.Contains(workspaceBoundary, labeler.QueryCalls);
        Assert.Contains(sandboxRoot, labeler.QueryCalls);
    }

    [Xunit.Fact]
    public void WorkspaceBoundary_Unknown_SetsMediumNonRecursive()
    {
        var parent = Path.Combine(Path.GetTempPath(), "mcg-boundary-tests", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(parent, "goal-worktree");
        Directory.CreateDirectory(worktree);
        var labeler = new RecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: false, Inheritable: false));
        try
        {
            DispatchProcessHost.IntegrityLabelerOverrideForTests = labeler;

            DispatchProcessHost.ProtectWorkspaceBoundary(worktree);

            Assert.Equal(parent, Assert.Single(labeler.QueryCalls));
            var parentCall = Assert.Single(labeler.SetCalls, call => call.Path == parent);
            Assert.Equal("M", parentCall.Level);
            Assert.False(parentCall.Recursive);
        }
        finally
        {
            DispatchProcessHost.IntegrityLabelerOverrideForTests = null;
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void WorkspaceBoundary_Medium_QueriesOnceAndSkipsSet()
    {
        var parent = Path.Combine(Path.GetTempPath(), "mcg-boundary-tests", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(parent, "goal-worktree");
        Directory.CreateDirectory(worktree);
        var labeler = new RecordingIntegrityLabeler(
            new IntegrityLabelState(Exists: true, Low: false, Inheritable: true, Medium: true));
        try
        {
            DispatchProcessHost.IntegrityLabelerOverrideForTests = labeler;

            DispatchProcessHost.ProtectWorkspaceBoundary(worktree);

            Assert.Equal(parent, Assert.Single(labeler.QueryCalls));
            Assert.Empty(labeler.SetCalls);
        }
        finally
        {
            DispatchProcessHost.IntegrityLabelerOverrideForTests = null;
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void WorkspaceBoundary_SetFailure_ThrowsAfterQuery()
    {
        var parent = Path.Combine(Path.GetTempPath(), "mcg-boundary-tests", Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(parent, "goal-worktree");
        Directory.CreateDirectory(worktree);
        var labeler = new RecordingIntegrityLabeler(
            new IntegrityLabelState(Exists: true, Low: false, Inheritable: false),
            setResult: false);
        try
        {
            DispatchProcessHost.IntegrityLabelerOverrideForTests = labeler;

            var failure = Assert.Throws<InvalidOperationException>(
                () => DispatchProcessHost.ProtectWorkspaceBoundary(worktree));

            Assert.Contains("workspace boundary", failure.Message);
            Assert.Equal(parent, Assert.Single(labeler.QueryCalls));
            var parentCall = Assert.Single(labeler.SetCalls, call => call.Path == parent);
            Assert.Equal("M", parentCall.Level);
            Assert.False(parentCall.Recursive);
        }
        finally
        {
            DispatchProcessHost.IntegrityLabelerOverrideForTests = null;
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
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

    private static string? ReadHeartbeatState(string path)
    {
        try
        {
            using var heartbeat = JsonDocument.Parse(File.ReadAllText(path));
            return heartbeat.RootElement.GetProperty("state").GetString();
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AssertNativeExitArtifact(string path, int expectedExitCode, int attempts = 5, int delayMs = 100)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (DispatchExitArtifacts.TryRead(path, out var artifact))
            {
                Assert.Equal(expectedExitCode, artifact.ExitCode);
                Assert.Equal(DispatchExitArtifactOrigin.Native, artifact.Origin);
                return;
            }

            if (i < attempts - 1) Thread.Sleep(delayMs);
        }

        Assert.Fail($"Could not read typed exit artifact '{path}'.");
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
