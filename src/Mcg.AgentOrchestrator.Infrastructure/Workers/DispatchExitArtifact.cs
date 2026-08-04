using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DispatchExitArtifact(
    int ExitCode,
    DispatchExitArtifactOrigin Origin,
    string Reason,
    DateTimeOffset RecordedAt,
    int Version = 1);

public static class DispatchExitArtifacts
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static DispatchExitArtifact Native(int exitCode, string reason, DateTimeOffset recordedAt) =>
        new(exitCode, DispatchExitArtifactOrigin.Native, reason, recordedAt);

    public static DispatchExitArtifact Synthetic(int exitCode, string reason, DateTimeOffset recordedAt) =>
        new(exitCode, DispatchExitArtifactOrigin.Synthetic, reason, recordedAt);

    public static void Write(string path, DispatchExitArtifact artifact)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var payload = JsonSerializer.Serialize(artifact, SerializerOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    public static bool TryRead(string path, out DispatchExitArtifact artifact)
    {
        artifact = default!;
        try
        {
            var payload = File.ReadAllText(path).Trim();
            if (int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacyExitCode))
            {
                artifact = new DispatchExitArtifact(
                    legacyExitCode,
                    DispatchExitArtifactOrigin.UnknownLegacy,
                    "legacy integer exit artifact",
                    File.GetLastWriteTimeUtc(path));
                return true;
            }

            var parsed = JsonSerializer.Deserialize<DispatchExitArtifact>(payload, SerializerOptions);
            if (parsed is null || parsed.Version != 1 || parsed.Origin == DispatchExitArtifactOrigin.None)
            {
                return false;
            }

            artifact = parsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }
}
