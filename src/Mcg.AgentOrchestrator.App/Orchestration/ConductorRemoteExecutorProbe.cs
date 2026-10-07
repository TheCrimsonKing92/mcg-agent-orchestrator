using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One advisory background call at a time. The tick only selects and schedules work.
internal sealed class ConductorRemoteExecutorProbe(
    string hostStateRoot, string configurationPath, TimeProvider clock,
    Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport,
    Func<int, DateTimeOffset?> startTimeOf, ConductEventLogWriter writer)
{
    private const string Script = "$ProgressPreference = 'SilentlyContinue'; $task = schtasks /query /tn mcg-executor-lane /fo csv /nh 2>$null | Select-Object -First 1; " +
        "$power = $null; try { $battery = Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus -ErrorAction Stop | Select-Object -First 1; " +
        "if ($null -ne $battery) { $power = [bool]$battery.PowerOnline } } catch {}; " +
        "[pscustomobject]@{ task = $task; powerOnline = $power } | ConvertTo-Json -Compress";
    private readonly Dictionary<string, DateTimeOffset> _starts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _reasons = new(StringComparer.Ordinal);
    private bool _seeded;
    internal Task? CurrentProbe { get; private set; }

    internal void Evaluate()
    {
        try
        {
            if (CurrentProbe is { IsCompleted: false }) return;
            if (!_seeded)
            {
                foreach (var row in RemoteExecutorReportReader.ReadProbes(
                    Path.Combine(hostStateRoot, ".orchestrator", RemoteExecutorProbeLedger.FileName), out _)
                    .OrderBy(row => row.ObservedAt))
                    _reasons[row.ExecutorId] = row.Reasons.Order(StringComparer.Ordinal).ToArray();
                _seeded = true;
            }
            var now = clock.GetUtcNow();
            var entry = RemoteLaneExecutorConfiguration.LoadExecutors(configurationPath).FirstOrDefault(entry =>
                entry.Transport == "ssh" && entry.AdminAlias is not null &&
                (!_starts.TryGetValue(entry.Id, out var last) || now - last >= TimeSpan.FromMinutes(10)) &&
                !RemoteExecutorOccupancy.IsOccupied(hostStateRoot, entry.Id, startTimeOf));
            if (entry is null) return;
            _starts[entry.Id] = now;
            CurrentProbe = Task.Run(() => ProbeAsync(entry));
        }
        catch (Exception) { }
    }

    private async Task ProbeAsync(RemoteLaneExecutorEntry entry)
    {
        try
        {
            var result = await transport([SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                entry.AdminAlias!, "powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))],
                hostStateRoot, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            var reachable = result.ExitCode != 255 && !result.TimedOut && !string.IsNullOrWhiteSpace(result.Output);
            var reasons = new List<string>();
            string? state = null;
            bool? power = null;
            if (!reachable) reasons.Add("unreachable");
            else if (result.ExitCode != 0) reasons.Add("probe-failed");
            else
            {
                try
                {
                    var jsonLine = result.Output.Split('\n').LastOrDefault(line => line.TrimStart().StartsWith('{'));
                    using var json = JsonDocument.Parse(jsonLine ?? "");
                    var root = json.RootElement;
                    var task = root.GetProperty("task");
                    var line = task.ValueKind == JsonValueKind.Null ? null : task.GetString();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var fields = Regex.Matches(line, "\"((?:[^\"]|\"\")*)\"")
                            .Select(match => match.Groups[1].Value.Replace("\"\"", "\"")).ToArray();
                        if (fields.Length >= 3) state = fields[2];
                    }
                    var online = root.GetProperty("powerOnline");
                    power = online.ValueKind == JsonValueKind.Null ? null : online.GetBoolean();
                    if (state is not ("Ready" or "Running")) reasons.Add("task-not-ready");
                    if (power == false) reasons.Add("on-battery");
                }
                catch (Exception) { state = null; power = null; reasons.Clear(); reasons.Add("probe-failed"); }
            }
            var now = clock.GetUtcNow();
            var stderr = PowerShellClixmlStandardError.Readable(result.Stderr ?? "");
            var row = new RemoteExecutorProbeRow(now, entry.Id, reachable, result.ExitCode, result.TimedOut,
                state, power, reasons.ToArray(), stderr.Length > 2048 ? stderr[^2048..] : stderr);
            RemoteExecutorProbeLedger.Append(Path.Combine(hostStateRoot, ".orchestrator", RemoteExecutorProbeLedger.FileName), row);
            var sorted = reasons.Order(StringComparer.Ordinal).ToArray();
            var previous = _reasons.GetValueOrDefault(entry.Id, []);
            if (!previous.SequenceEqual(sorted, StringComparer.Ordinal))
            {
                var powerText = power?.ToString().ToLowerInvariant() ?? "unknown";
                var detail = sorted.Length == 0
                    ? $"REMOTE_EXECUTOR_RECOVERED executor={entry.Id} task_state={state ?? "unknown"} power_online={powerText}"
                    : $"REMOTE_EXECUTOR_WARNING executor={entry.Id} reachable={reachable.ToString().ToLowerInvariant()} task_state={state ?? "unknown"} power_online={powerText} reasons={string.Join(",", reasons)}";
                writer.AppendRequired("remote-executor-health", null, detail, now, $"remote-executor-health:{Guid.NewGuid():N}");
            }
            _reasons[entry.Id] = sorted;
        }
        catch (Exception) { }
    }
}
