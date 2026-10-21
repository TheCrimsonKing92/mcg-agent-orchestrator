using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Observations from one integrity-label command; never compiler or completion authority.
internal sealed record IntegrityLabelCommandResult(bool Succeeded, string Diagnostic)
{
    internal static IntegrityLabelCommandResult Failed(Exception? exception) => new(false,
        JsonSerializer.Serialize(new
        {
            completed = false, exitCode = (int?)null,
            exceptionType = exception?.GetType().Name ?? "ProcessStartReturnedNull",
            exceptionMessage = Limit(exception?.Message ?? "No process was returned.")
        }));

    internal static IntegrityLabelCommandResult Completed(bool completed, int? exitCode,
        bool drainsCompleted, OutputCapture output, OutputCapture error) =>
        new(completed && exitCode == 0, JsonSerializer.Serialize(new
        {
            completed, exitCode, timedOut = !completed, drainsCompleted,
            stdoutTail = output.Snapshot(), stderrPrefix = error.Snapshot()
        }));

    private static string Limit(string value) => value[..Math.Min(value.Length, 4096)];

    internal sealed class OutputCapture(bool keepTail)
    {
        private readonly object sync = new();
        private readonly StringBuilder text = new();

        internal async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[1024];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            {
                lock (sync)
                {
                    if (keepTail)
                    {
                        text.Append(buffer, 0, read);
                        if (text.Length > 4096) text.Remove(0, text.Length - 4096);
                    }
                    else
                    {
                        text.Append(buffer, 0, Math.Min(read, 4096 - text.Length));
                    }
                }
            }
        }

        internal string Snapshot()
        {
            lock (sync) return text.ToString();
        }
    }
}