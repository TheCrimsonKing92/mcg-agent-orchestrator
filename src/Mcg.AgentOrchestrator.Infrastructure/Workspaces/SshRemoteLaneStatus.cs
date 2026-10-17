using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Executor-owned commitSha and treeSha are deliberately never synthesized from the request.
internal sealed record SshRemoteLaneStatus(JsonElement Fields)
{
    internal string String(string name) => Fields.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    internal static SshRemoteLaneStatus? Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object ? new(document.RootElement.Clone()) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    internal (int ExitCode, string[] Names) Completion()
    {
        if (!Fields.TryGetProperty("exitCode", out var exit) || exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out var code))
            throw new RemoteLaneTransportException("missing-or-invalid-exitCode");
        if (!Fields.TryGetProperty("trx", out var trx)) throw new RemoteLaneTransportException("missing-trx");
        JsonElement[] entries = trx.ValueKind switch
        {
            JsonValueKind.String => [trx],
            JsonValueKind.Array => trx.EnumerateArray().ToArray(),
            _ => throw new RemoteLaneTransportException("invalid-trx-name: non-string")
        };
        if (entries.Length == 0)
        {
            if (code != 0) return (code, []);
            throw new RemoteLaneTransportException("no-trx-produced exitCode=0");
        }
        var names = new string[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].ValueKind != JsonValueKind.String)
                throw new RemoteLaneTransportException("invalid-trx-name: non-string");
            var name = entries[index].GetString()!;
            if (!Regex.IsMatch(name, "^[A-Za-z0-9._-]+\\.trx$", RegexOptions.CultureInvariant) || name.Contains('\n'))
                throw new RemoteLaneTransportException($"invalid-trx-name: {RemoteLaneStatusValueText.Sanitize(name)}");
            names[index] = name;
        }
        return (code, names);
    }
    internal RemoteLaneResult Result(int code, string[] paths) => new(String("executorId"), String("lane"),
        String("filterHash"), String("commitSha"), String("treeSha"), String("mainSha"), String("manifestIdentity"), code, paths);
}
