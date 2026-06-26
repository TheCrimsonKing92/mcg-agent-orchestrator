using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalMonitoringSubscriptionCommand
{
    private const string OnceFlag = "--once";
    private const string SinceFlag = "--since";

    public static GoalMonitoringSubscriptionOptions Parse(IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            throw new ArgumentException(Usage);
        }

        var sinceEventId = 0L;
        var once = false;
        Uri? dashboardUri = null;
        string goalId;
        var optionStart = 2;
        if (IsDashboardUri(parts[1]))
        {
            if (parts.Count < 3)
            {
                throw new ArgumentException(Usage);
            }

            dashboardUri = CreateDashboardUri(parts[1]);
            goalId = parts[2];
            optionStart = 3;
        }
        else
        {
            goalId = parts[1];
        }

        for (var index = optionStart; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Equals(OnceFlag, StringComparison.OrdinalIgnoreCase))
            {
                once = true;
                continue;
            }

            if (part.Equals(SinceFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= parts.Count || !long.TryParse(parts[index + 1], out sinceEventId) || sinceEventId < 0)
                {
                    throw new ArgumentException(Usage);
                }

                index++;
                continue;
            }

            throw new ArgumentException($"Unknown monitor-goal option '{part}'.");
        }

        return new GoalMonitoringSubscriptionOptions(
            dashboardUri,
            goalId,
            sinceEventId,
            once);
    }

    public static Uri BuildSnapshotUri(GoalMonitoringSubscriptionOptions options)
    {
        return BuildUri(options, stream: false);
    }

    public static Uri BuildStreamUri(GoalMonitoringSubscriptionOptions options)
    {
        return BuildUri(options, stream: true);
    }

    public static async Task RunAsync(IReadOnlyList<string> parts, TextWriter output, CancellationToken cancellationToken = default)
    {
        var options = Parse(parts);
        if (options.IsLocal)
        {
            throw new ArgumentException("Local monitor-goal requires orchestrator state. Use: monitor-goal <goal-id> [--since <event-id>] [--once]");
        }

        using var client = new HttpClient();

        if (options.Once)
        {
            var batch = await client.GetFromJsonAsync<GoalMonitoringBatchDto>(
                BuildSnapshotUri(options),
                DashboardJson.Options(),
                cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                throw new InvalidOperationException("Dashboard returned an empty monitoring response.");
            }

            PrintBatch(batch, output);
            return;
        }

        await using var stream = await client.GetStreamAsync(BuildStreamUri(options), cancellationToken).ConfigureAwait(false);
        await foreach (var serverEvent in ReadServerSentEventsAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            PrintServerSentEvent(serverEvent, output);
        }
    }

    public static async Task RunAsync(
        IReadOnlyList<string> parts,
        TextWriter output,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        Func<AgentOrchestratorKernel>? reloadKernel = null,
        CancellationToken cancellationToken = default)
    {
        var options = Parse(parts);
        if (!options.IsLocal)
        {
            await RunAsync(parts, output, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var stream = new TextWriterStream(output);
        var runEvents = new SqliteRunEventStore(workspace.RunEventStorePath);
        await GoalMonitoringStream.StreamAsync(
            stream,
            options.GoalId,
            _ => Task.FromResult(reloadKernel?.Invoke() ?? kernel),
            (current, goal, since) => GoalMonitoringStream.BuildBatch(current, goal, since, agents, workerProfiles, workspace),
            runEvents,
            options.SinceEventId,
            options.Once,
            TimeSpan.FromSeconds(1),
            cancellationToken).ConfigureAwait(false);
    }

    public static void PrintBatch(GoalMonitoringBatchDto batch, TextWriter output)
    {
        PrintSnapshot(batch.Snapshot, output);
        foreach (var evt in batch.Events)
        {
            PrintTimelineEvent(evt, output);
        }
    }

    public static void PrintServerSentEvent(ServerSentEvent serverEvent, TextWriter output)
    {
        if (serverEvent.Event.Equals("goal.snapshot", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = JsonSerializer.Deserialize<GoalMonitoringSnapshotDto>(serverEvent.Data, DashboardJson.Options());
            if (snapshot is not null)
            {
                PrintSnapshot(snapshot, output);
            }

            return;
        }

        if (serverEvent.Event.Equals("timeline", StringComparison.OrdinalIgnoreCase))
        {
            var evt = JsonSerializer.Deserialize<GoalMonitoringEventDto>(serverEvent.Data, DashboardJson.Options());
            if (evt is not null)
            {
                PrintTimelineEvent(evt, output);
            }

            return;
        }

        output.WriteLine($"event {serverEvent.Event} id={serverEvent.Id ?? "-"}");
    }

    public static async IAsyncEnumerable<ServerSentEvent> ReadServerSentEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? id = null;
        string? eventName = null;
        var data = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (eventName is not null || data.Length > 0)
                {
                    yield return new ServerSentEvent(id, eventName ?? "message", data.ToString());
                }

                id = null;
                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                id = line["id:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.AppendLine();
                }

                data.Append(line["data:".Length..].TrimStart());
            }
        }
    }

    private static void PrintSnapshot(GoalMonitoringSnapshotDto snapshot, TextWriter output)
    {
        var running = snapshot.Tasks.Count(task => task.Status == WorkTaskStatus.Running);
        var failed = snapshot.Tasks.Count(task => task.Status == WorkTaskStatus.Failed);
        var completed = snapshot.Tasks.Count(task => task.Status == WorkTaskStatus.Completed);
        var capacity = snapshot.ProviderCapacity is null
            ? string.Empty
            : $" capacity={snapshot.ProviderCapacity.Disposition} ready={snapshot.ProviderCapacity.ReadyNowCount} deferred={snapshot.ProviderCapacity.DeferredCount}";
        output.WriteLine(
            $"snapshot goal={snapshot.GoalId[..Math.Min(8, snapshot.GoalId.Length)]} status={snapshot.Monitor.Status} tasks={snapshot.Tasks.Count} completed={completed} running={running} failed={failed} lastEvent={snapshot.LastEventId} attention={snapshot.Monitor.Attention.Count} inbox={snapshot.OperatorInbox?.OpenCount ?? 0}{capacity}");
    }

    private static void PrintTimelineEvent(GoalMonitoringEventDto evt, TextWriter output)
    {
        var scope = evt.TaskNumber is null ? "goal" : $"task {evt.TaskNumber}";
        output.WriteLine($"event {evt.Id} {evt.OccurredAt:u} {evt.Kind} {scope}: {evt.Message}");
    }

    private static Uri BuildUri(GoalMonitoringSubscriptionOptions options, bool stream)
    {
        if (options.DashboardUri is null)
        {
            throw new InvalidOperationException("Dashboard URI is not available for local monitor-goal mode.");
        }

        var goalId = Uri.EscapeDataString(options.GoalId);
        var path = stream ? $"api/goals/{goalId}/events/stream" : $"api/goals/{goalId}/events";
        var builder = new UriBuilder(new Uri(options.DashboardUri, path));
        if (options.SinceEventId > 0)
        {
            builder.Query = $"since={options.SinceEventId}";
        }

        return builder.Uri;
    }

    private static bool IsDashboardUri(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static Uri CreateDashboardUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Dashboard URL must be an absolute http or https URL.");
        }

        return value.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(value + "/");
    }

    private const string Usage = "Usage: monitor-goal <goal-id> [--since <event-id>] [--once], or monitor-goal <dashboard-url> <goal-id> [--since <event-id>] [--once]";

    private sealed class TextWriterStream(TextWriter writer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => writer.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => writer.Write(Encoding.UTF8.GetString(buffer, offset, count));
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await writer.WriteAsync(Encoding.UTF8.GetString(buffer.Span)).ConfigureAwait(false);
        }
    }
}

internal sealed record GoalMonitoringSubscriptionOptions(Uri? DashboardUri, string GoalId, long SinceEventId, bool Once)
{
    public bool IsLocal => DashboardUri is null;
}

internal sealed record ServerSentEvent(string? Id, string Event, string Data);
