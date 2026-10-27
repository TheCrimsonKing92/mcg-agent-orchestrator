using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class NormalizedTestResultWriter
{
    internal const string FileSuffix = ".ctrf.json";

    internal static string PathFor(string reportPath) => Path.Combine(
        Path.GetDirectoryName(reportPath) ?? "", Path.GetFileNameWithoutExtension(reportPath) + FileSuffix);

    internal static string Serialize(string toolName, IReadOnlyList<TestResult> records)
    {
        if (string.IsNullOrEmpty(toolName))
            throw new ArgumentException("Tool name must not be empty.", nameof(toolName));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.Id) || string.IsNullOrEmpty(record.DisplayName))
                throw new ArgumentException($"Test '{record.Id}' must have a non-empty Id and DisplayName.", nameof(records));
            if (!ids.Add(record.Id))
                throw new ArgumentException($"Repeated test Id '{record.Id}'.", nameof(records));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("reportFormat", "CTRF");
            writer.WriteString("schemaVersion", NormalizedTestResultSource.SchemaVersion);
            writer.WriteStartObject("results");
            writer.WriteStartObject("tool");
            writer.WriteString("name", toolName);
            writer.WriteEndObject();
            writer.WriteStartArray("tests");
            foreach (var record in records)
            {
                writer.WriteStartObject();
                writer.WriteString("testId", record.Id);
                writer.WriteString("name", record.DisplayName);
                writer.WriteString("status", NormalizedTestResultSource.StatusValues[(int)record.Status]);
                writer.WriteStartArray("suite");
                foreach (var part in record.SuitePath) writer.WriteStringValue(part);
                writer.WriteEndArray();
                WriteDuration(writer, record.DurationMs);
                WriteText(writer, "filePath", record.File);
                WriteText(writer, "message", record.Message);
                WriteText(writer, "trace", record.Trace);
                if (record.Attempts.Count > 0)
                {
                    writer.WriteStartArray("retryAttempts");
                    foreach (var attempt in record.Attempts)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("status", NormalizedTestResultSource.StatusValues[(int)attempt.Status]);
                        WriteDuration(writer, attempt.DurationMs);
                        WriteText(writer, "message", attempt.Message);
                        WriteText(writer, "trace", attempt.Trace);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteStartObject("extra");
                writer.WriteString("reportedBy", record.ReportedBy);
                WriteText(writer, "seed", record.Seed);
                WriteText(writer, "outputRef", record.OutputRef);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void Write(string targetPath, string toolName, IReadOnlyList<TestResult> records)
    {
        var temporaryPath = targetPath + ".tmp";
        try
        {
            var json = Serialize(toolName, records);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporaryPath); }
            catch { /* Cleanup must not replace the original failure. */ }
            throw;
        }
    }

    private static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null) writer.WriteString(name, value);
    }

    private static void WriteDuration(Utf8JsonWriter writer, long? value)
    {
        if (value is { } duration) writer.WriteNumber("duration", duration);
    }
}
