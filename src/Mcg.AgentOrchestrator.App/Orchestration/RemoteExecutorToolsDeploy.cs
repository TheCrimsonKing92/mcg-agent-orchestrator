using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ExecutorToolsDeployResult(string Outcome, string? Reason, string? Previous,
    string? Readback, string? Backup, string? ErrorTail, string? Detail = null);

internal sealed class RemoteExecutorToolsDeploy(
    Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport,
    TimeSpan readBound, TimeSpan bound)
{
    internal string? ReadDeployedDigest(RemoteLaneExecutorEntry entry, string localFolder)
    {
        try
        {
            Directory.CreateDirectory(localFolder);
            var manifest = Path.Combine(localFolder, ExecutorToolsDigest.ManifestFileName);
            File.Delete(manifest);
            var result = transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
                $"{entry.AdminAlias}:{entry.RunRoot!.TrimEnd('/', '\\')}/{ExecutorToolsDigest.ManifestFileName}",
                ExecutorToolsDigest.ManifestFileName], localFolder, readBound, CancellationToken.None).GetAwaiter().GetResult();
            return result.ExitCode == 0 && !result.TimedOut && File.Exists(manifest)
                ? ExecutorToolsDigest.ReadManifest(File.ReadAllText(manifest)) : null;
        }
        catch (Exception) { return null; }
    }

    internal ExecutorToolsDeployResult Deploy(RemoteLaneExecutorEntry entry, ExecutorToolsSource source, string backupName)
    {
        // The source is a reusable committed-main cache. Only these new paths belong to this attempt.
        var attempt = "tools-deploy-" + Guid.NewGuid().ToString("N");
        var local = Path.Combine(Path.GetTempPath(), attempt);
        var remoteTouched = false;
        var localCleanupFailed = false;
        ExecutorToolsDeployResult result;
        try
        {
            if (!SafeRemoteRoot(entry.RunRoot) || !SafeBackupName(backupName)) return Failed("unsafe-path", null);
            Directory.CreateDirectory(local);
            var scriptName = attempt + ".ps1";
            File.WriteAllText(Path.Combine(local, scriptName), Script(entry.RunRoot!, attempt, source.Digest, backupName),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            remoteTouched = true; // SCP can leave a partial destination even when it fails.
            result = DeployAttempt(entry, source, attempt, local, scriptName);
        }
        catch (Exception error) { result = TransportFailure(error); }
        finally
        {
            // There is one local file, never a recursive delete of a computed source/cache path.
            try
            {
                var file = Path.Combine(local, attempt + ".ps1");
                if (File.Exists(file)) File.Delete(file);
                if (Directory.Exists(local)) Directory.Delete(local);
            }
            catch (Exception) { localCleanupFailed = true; }
        }
        if (localCleanupFailed) result = CleanupFailed(result, "local-file");
        if (remoteTouched)
        {
            try
            {
                var cleanup = transport(Ssh(entry, "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(CleanupScript(entry.RunRoot!, attempt)))),
                    source.Folder, readBound, CancellationToken.None).GetAwaiter().GetResult();
                if (cleanup.ExitCode != 0 || cleanup.TimedOut)
                    result = CleanupFailed(result, cleanup.TimedOut ? "timed-out" : "remote-exit");
            }
            catch (Exception error)
            {
                result = CleanupFailed(result, error is CommandLineTooLongException limit ? LimitDetail(limit) : "transport-error");
            }
        }
        return result;
    }

    private ExecutorToolsDeployResult DeployAttempt(RemoteLaneExecutorEntry entry, ExecutorToolsSource source,
        string attempt, string local, string scriptName)
    {
            var copy = transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", "-r", source.Folder,
                Destination(entry, attempt + ".staging")], local, bound,
                CancellationToken.None).GetAwaiter().GetResult();
            if (copy.ExitCode != 0 || copy.TimedOut) return Failed("copy-failed", copy.Stderr);
            var upload = transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", scriptName,
                Destination(entry, scriptName)], local, bound, CancellationToken.None).GetAwaiter().GetResult();
            if (upload.ExitCode != 0 || upload.TimedOut) return Failed("script-copy-failed", upload.Stderr);
            var result = transport(Ssh(entry, "-File", "\"" + RemotePath(entry, scriptName) + "\""),
                local, bound, CancellationToken.None).GetAwaiter().GetResult();
            if (result.TimedOut) return Failed("timed-out", result.Stderr);
            if (result.ExitCode == 255 || string.IsNullOrWhiteSpace(result.Output)) return Failed("unreachable", result.Stderr);
            if (result.ExitCode != 0) return Failed("remote-exit-" + result.ExitCode.ToString(CultureInfo.InvariantCulture), result.Stderr);
            try
            {
                var line = result.Output.Split('\n').LastOrDefault(value => value.TrimStart().StartsWith('{'));
                using var json = JsonDocument.Parse(line ?? "");
                var root = json.RootElement;
                var previous = Text(root, "previous");
                var backup = Text(root, "backup");
                var readback = Text(root, "readback")?.Trim();
                if (Text(root, "error") is { } error)
                {
                    // Remote output is untrusted diagnostic input; only the script's closed error vocabulary is durable.
                    if (error is not ("runner-busy" or "staged-digest-mismatch" or "backup-exists" or "swap-failed" or
                        "rollback-failed" or "cleanup-failed" or "deployment-failed")) return Failed("output-unparsable", result.Stderr);
                    var failed = Failed("remote-" + error, result.Stderr) with { Previous = previous, Readback = readback, Backup = backup };
                    return Text(root, "cleanup") == "failed" ? CleanupFailed(failed, "script") : failed;
                }
                if (readback is null) return Failed("output-unparsable", result.Stderr);
                if (!string.Equals(readback, source.Digest, StringComparison.OrdinalIgnoreCase))
                    return Failed("readback-mismatch", result.Stderr) with { Previous = previous, Readback = readback, Backup = backup };
                return new("deployed", null, previous, readback, backup, null);
            }
            catch (JsonException) { return Failed("output-unparsable", result.Stderr); }
            catch (InvalidOperationException) { return Failed("output-unparsable", result.Stderr); }
    }

    private static bool SafeRemoteRoot(string? path) => path is { Length: > 3 } && char.IsAsciiLetter(path[0]) &&
        path[1] == ':' && (path[2] is '/' or '\\') &&
        !path.Any(c => char.IsControl(c) || "\"%!?&|<>^`$;".Contains(c)) &&
        !path.Replace('\\', '/').Split('/').Any(part => part is "." or "..");
    private static bool SafeBackupName(string name) => name.StartsWith("tools.bak-", StringComparison.Ordinal) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
    private static string RemotePath(RemoteLaneExecutorEntry entry, string name) => entry.RunRoot!.TrimEnd('/', '\\').Replace('\\', '/') + "/" + name;
    private static string Destination(RemoteLaneExecutorEntry entry, string name) => $"{entry.AdminAlias}:{RemotePath(entry, name)}";
    private static string[] Ssh(RemoteLaneExecutorEntry entry, string mode, string value) =>
        [SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", entry.AdminAlias!,
            "powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", mode, value];
    private static string LimitDetail(CommandLineTooLongException error) => string.Create(CultureInfo.InvariantCulture,
        $"command-line-too-long length={error.Length} limit={error.Limit}");
    private static ExecutorToolsDeployResult TransportFailure(Exception error) => Failed("transport-error", error.Message) with
        { Detail = error is CommandLineTooLongException limit ? LimitDetail(limit) : "transport-exception" };
    private static ExecutorToolsDeployResult CleanupFailed(ExecutorToolsDeployResult result, string detail) => result with
    {
        Outcome = "failed", Reason = result.Reason ?? "cleanup-failed",
        Detail = (result.Detail is null ? "" : result.Detail + "; ") + "cleanup-failed " + detail
    };

    private static ExecutorToolsDeployResult Failed(string reason, string? error)
    {
        var tail = PowerShellClixmlStandardError.Readable(error ?? "");
        return new("failed", reason, null, null, null,
            string.IsNullOrWhiteSpace(tail) ? null : tail.Length > 2048 ? tail[^2048..] : tail,
            string.IsNullOrWhiteSpace(tail) ? null : "remote-stderr-present");
    }

    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    // Reject links throughout the target tree and its ancestors before any recursive removal.
    // Reused verbatim by the uploaded script and the bounded failure-path cleanup command.
    private const string PathGuard = """
        function Assert-Tree($p, $n) {
            $p = [IO.Path]::GetFullPath($p)
            if ($p -ne [IO.Path]::GetFullPath((Join-Path $root $n)) -or [IO.Path]::GetDirectoryName($p) -ne $root) { throw 'unsafe-path' }
            $a = $p
            while ($a) {
                if (Test-Path -LiteralPath $a) {
                    if ((Get-Item -LiteralPath $a -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'unsafe-link' }
                }
                $a = [IO.Path]::GetDirectoryName($a)
            }
            if (Test-Path -LiteralPath $p -PathType Container) {
                $q = New-Object 'Collections.Generic.Queue[string]'
                $q.Enqueue($p)
                while ($q.Count) {
                    foreach ($i in @(Get-ChildItem -LiteralPath $q.Dequeue() -Force)) {
                        if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'unsafe-link' }
                        if ($i.PSIsContainer) { $q.Enqueue($i.FullName) }
                    }
                }
            }
        }
        function Remove-Attempt {
            if ($attempt -cnotmatch '^tools-deploy-[a-f0-9]{32}$') { throw 'unsafe-attempt' }
            foreach ($suffix in @('.staging', '.ps1')) {
                $n = $attempt + $suffix
                $p = Join-Path $root $n
                Assert-Tree $p $n
                if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop }
            }
        }
        """;

    internal static string CleanupScript(string runRoot, string attempt) => $$"""
        $ErrorActionPreference = 'Stop'
        $root = [IO.Path]::GetFullPath({{Literal(runRoot)}}).TrimEnd('\','/')
        $attempt = {{Literal(attempt)}}
        {{PathGuard}}
        Remove-Attempt
        """;

    internal static string Script(string runRoot, string attempt, string digest, string backupName) => $$"""
        $ErrorActionPreference = 'Stop'
        $root = [IO.Path]::GetFullPath({{Literal(runRoot)}}).TrimEnd('\','/')
        $attempt = {{Literal(attempt)}}
        $staging = Join-Path $root ($attempt + '.staging')
        $tools = Join-Path $root 'tools'
        $name = {{Literal(backupName)}}
        $bak = Join-Path $root $name
        $manifest = Join-Path $root 'tools-digest.json'
        $expected = {{Literal(digest)}}
        {{PathGuard}}
        function Set-ScriptSet($from, $to) {
            $files = @(Get-ChildItem -LiteralPath $from -File -Filter '*.ps1')
            foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination $to -Force -ErrorAction Stop }
            foreach ($file in @(Get-ChildItem -LiteralPath $to -File -Filter '*.ps1')) {
                if ($files.Name -notcontains $file.Name) { Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop }
            }
        }
        $receipt = $null
        try {
            if ($attempt -cnotmatch '^tools-deploy-[a-f0-9]{32}$' -or $name -cnotmatch '^tools\.bak-[a-zA-Z0-9.-]+$') { throw 'unsafe-attempt' }
            Assert-Tree $staging ($attempt + '.staging')
            Assert-Tree $tools 'tools'
            Assert-Tree $bak $name
            Assert-Tree $manifest 'tools-digest.json'
            $statePath = Join-Path $root 'runner-state.json'
            if (Test-Path -LiteralPath $statePath) {
                $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                $written = (Get-Item -LiteralPath $statePath).LastWriteTimeUtc
                if ($state.utc) { $written = ([datetimeoffset]$state.utc).UtcDateTime }
                if (([datetime]::UtcNow - $written).TotalSeconds -lt 120 -and (@($state.running).Count -gt 0 -or @($state.queued).Count -gt 0)) {
                    $receipt = [pscustomobject]@{error='runner-busy'}
                    return
                }
            }
            $stagedManifest = Join-Path $staging 'tools-staging-digest.json'
            & (Join-Path $staging 'Get-ExecutorToolsDigest.ps1') -Path $staging -OutFile $stagedManifest | Out-Null
            $staged = (Get-Content -LiteralPath $stagedManifest -Raw | ConvertFrom-Json).digest
            if ($staged -ne $expected) { $receipt = [pscustomobject]@{error='staged-digest-mismatch';staged=$staged}; return }
            if (Test-Path -LiteralPath $bak) { $receipt = [pscustomobject]@{error='backup-exists'}; return }
            $original = $null
            $previous = $null
            if (Test-Path -LiteralPath $manifest) {
                $original = [IO.File]::ReadAllBytes($manifest)
                try { $previous = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).digest } catch {}
            }
            Copy-Item -LiteralPath $tools -Destination $bak -Recurse -ErrorAction Stop
            try {
                Set-ScriptSet $staging $tools
                & (Join-Path $tools 'Get-ExecutorToolsDigest.ps1') -Path $tools -OutFile $manifest | Out-Null
                $readback = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).digest
                if ($readback -ne $expected) { throw 'tools readback mismatch' }
                $receipt = [pscustomobject]@{previous=$previous;backup=$name;readback=$readback}
            } catch {
                try {
                    Assert-Tree $bak $name
                    Assert-Tree $tools 'tools'
                    Set-ScriptSet $bak $tools
                    if ($null -ne $original) { [IO.File]::WriteAllBytes($manifest, $original) }
                    elseif (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }
                    $receipt = [pscustomobject]@{error='swap-failed';previous=$previous;backup=$name}
                } catch {
                    $receipt = [pscustomobject]@{error='rollback-failed';previous=$previous;backup=$name}
                }
            }
        } catch {
            $receipt = [pscustomobject]@{error='deployment-failed'}
        } finally {
            try { Remove-Attempt } catch {
                if ($null -eq $receipt -or !$receipt.error) { $receipt = [pscustomobject]@{error='cleanup-failed';backup=$name} }
                $receipt | Add-Member -NotePropertyName cleanup -NotePropertyValue 'failed' -Force
            }
            $receipt | ConvertTo-Json -Compress
        }
        """;
}
