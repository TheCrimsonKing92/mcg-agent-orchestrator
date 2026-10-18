using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record RemoteSlotsSyncResult(string Outcome, string? Reason, string? RemoteBackup,
    string? Previous, string? ErrorTail);

// One bounded call writes and verifies the executor's capacity file.
internal sealed class RemoteExecutorSlotsFileSync(
    Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport,
    string workingDirectory, TimeSpan bound)
{
    internal TimeSpan Bound => bound;

    internal RemoteSlotsSyncResult Sync(RemoteLaneExecutorEntry entry, int slots, string backupName)
    {
        GoalAcceptanceVerifier.CommandResult result;
        try
        {
            result = transport([SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                entry.AdminAlias!, "powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(entry.RunRoot!, slots, backupName)))],
                workingDirectory, Bound, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception error) { return new("failed", "transport-error", null, null, Tail(error.Message)); }

        var errorTail = Tail(PowerShellClixmlStandardError.Readable(result.Stderr ?? ""));
        RemoteSlotsSyncResult Failed(string reason) => new("failed", reason, null, null, errorTail);
        if (result.TimedOut) return Failed("timed-out");
        if (result.ExitCode == 255 || string.IsNullOrWhiteSpace(result.Output)) return Failed("unreachable");
        if (result.ExitCode != 0) return Failed("remote-exit-" + result.ExitCode.ToString(CultureInfo.InvariantCulture));
        try
        {
            var line = result.Output.Split('\n').LastOrDefault(value => value.TrimStart().StartsWith('{'));
            using var json = JsonDocument.Parse(line ?? "");
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                error.GetString() == "backup-exists") return Failed("remote-backup-exists");
            if (!root.TryGetProperty("readback", out var readback)) return Failed("output-unparsable");
            if (readback.ValueKind != JsonValueKind.String || !string.Equals(readback.GetString()?.Trim(),
                    slots.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) return Failed("readback-mismatch");
            return new("synced", null, Text(root, "backup"), Text(root, "previous")?.Trim(), null);
        }
        catch (JsonException) { return Failed("output-unparsable"); }
        catch (InvalidOperationException) { return Failed("output-unparsable"); }
    }

    private static string? Text(JsonElement root, string name) => !root.TryGetProperty(name, out var value) ||
        value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();

    private static string? Tail(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Length > 2048 ? text[^2048..] : text;
    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Script(string runRoot, int slots, string backupName) => $$"""
        $ErrorActionPreference = 'Stop'
        $root = {{Literal(runRoot)}}
        $name = {{Literal(backupName)}}
        $file = Join-Path $root 'slots.txt'
        $bak = Join-Path $root $name
        $tmp = Join-Path $root {{Literal(Path.ChangeExtension(backupName, ".tmp"))}}
        if (Test-Path -LiteralPath $bak) { [pscustomobject]@{error='backup-exists'} | ConvertTo-Json -Compress; exit 0 }
        $previous = $null
        $backup = $null
        if (Test-Path -LiteralPath $file) {
            $previous = ([string](Get-Content -LiteralPath $file -Raw)).Trim()
            Copy-Item -LiteralPath $file -Destination $bak -ErrorAction Stop
            $backup = $name
        }
        try {
            Set-Content -LiteralPath $tmp -Value '{{slots.ToString(CultureInfo.InvariantCulture)}}' -NoNewline -Encoding ascii
            Move-Item -LiteralPath $tmp -Destination $file -Force
            [pscustomobject]@{previous=$previous;backup=$backup;readback=([string](Get-Content -LiteralPath $file -Raw)).Trim()} | ConvertTo-Json -Compress
        } finally { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp } }
        """;
}
