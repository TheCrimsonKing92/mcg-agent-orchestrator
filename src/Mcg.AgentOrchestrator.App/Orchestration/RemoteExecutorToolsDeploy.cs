using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ExecutorToolsDeployResult(string Outcome, string? Reason, string? Previous,
    string? Readback, string? Backup, string? ErrorTail);

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
        try
        {
            var name = Path.GetFileName(source.Folder);
            var directory = Path.GetDirectoryName(source.Folder)!;
            var copy = transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", "-r", name,
                $"{entry.AdminAlias}:{entry.RunRoot!.TrimEnd('/', '\\')}/"], directory, bound,
                CancellationToken.None).GetAwaiter().GetResult();
            if (copy.ExitCode != 0 || copy.TimedOut) return Failed("copy-failed", copy.Stderr);
            var result = transport([SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                entry.AdminAlias!, "powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(entry.RunRoot, name, source.Digest, backupName)))],
                directory, bound, CancellationToken.None).GetAwaiter().GetResult();
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
                if (Text(root, "error") is { } error) return Failed("remote-" + error, result.Stderr);
                if (readback is null) return Failed("output-unparsable", result.Stderr);
                if (!string.Equals(readback, source.Digest, StringComparison.OrdinalIgnoreCase))
                    return Failed("readback-mismatch", result.Stderr) with { Previous = previous, Readback = readback, Backup = backup };
                return new("deployed", null, previous, readback, backup, null);
            }
            catch (JsonException) { return Failed("output-unparsable", result.Stderr); }
            catch (InvalidOperationException) { return Failed("output-unparsable", result.Stderr); }
        }
        catch (Exception error) { return Failed("transport-error", error.Message); }
    }

    private static ExecutorToolsDeployResult Failed(string reason, string? error)
    {
        var tail = PowerShellClixmlStandardError.Readable(error ?? "");
        return new("failed", reason, null, null, null,
            string.IsNullOrWhiteSpace(tail) ? null : tail.Length > 2048 ? tail[^2048..] : tail);
    }

    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Script(string runRoot, string stagingName, string digest, string backupName) => $$"""
        $ErrorActionPreference = 'Stop'
        $root = {{Literal(runRoot)}}
        $staging = Join-Path $root {{Literal(stagingName)}}
        $tools = Join-Path $root 'tools'
        $name = {{Literal(backupName)}}
        $bak = Join-Path $root $name
        $manifest = Join-Path $root 'tools-digest.json'
        $expected = {{Literal(digest)}}
        function Set-ScriptSet($from, $to) {
            $files = @(Get-ChildItem -LiteralPath $from -File -Filter '*.ps1')
            foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination $to -Force -ErrorAction Stop }
            foreach ($file in @(Get-ChildItem -LiteralPath $to -File -Filter '*.ps1')) {
                if ($files.Name -notcontains $file.Name) { Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop }
            }
        }
        try {
            $statePath = Join-Path $root 'runner-state.json'
            if (Test-Path -LiteralPath $statePath) {
                $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                $written = (Get-Item -LiteralPath $statePath).LastWriteTimeUtc
                if ($state.utc) { $written = ([datetimeoffset]$state.utc).UtcDateTime }
                if (([datetime]::UtcNow - $written).TotalSeconds -lt 120 -and (@($state.running).Count -gt 0 -or @($state.queued).Count -gt 0)) {
                    [pscustomobject]@{error='runner-busy'} | ConvertTo-Json -Compress; return
                }
            }
            $stagedManifest = Join-Path $root 'tools-staging-digest.json'
            & (Join-Path $staging 'Get-ExecutorToolsDigest.ps1') -Path $staging -OutFile $stagedManifest | Out-Null
            $staged = (Get-Content -LiteralPath $stagedManifest -Raw | ConvertFrom-Json).digest
            if ($staged -ne $expected) { [pscustomobject]@{error='staged-digest-mismatch';staged=$staged} | ConvertTo-Json -Compress; return }
            if (Test-Path -LiteralPath $bak) { [pscustomobject]@{error='backup-exists'} | ConvertTo-Json -Compress; return }
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
                [pscustomobject]@{previous=$previous;backup=$name;readback=$readback} | ConvertTo-Json -Compress
            } catch {
                Set-ScriptSet $bak $tools
                if ($null -ne $original) { [IO.File]::WriteAllBytes($manifest, $original) }
                elseif (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }
                [pscustomobject]@{error='swap-failed'} | ConvertTo-Json -Compress
            }
        } finally {
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        }
        """;
}
