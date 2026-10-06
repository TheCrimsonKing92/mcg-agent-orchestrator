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
        string[] names = trx.ValueKind switch
        {
            JsonValueKind.String => [trx.GetString()!],
            JsonValueKind.Array => trx.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : "").ToArray(),
            _ => []
        };
        if (names.Length == 0 || names.Any(name => !Regex.IsMatch(name, "^[A-Za-z0-9._-]+\\.trx$", RegexOptions.CultureInvariant) || name.Contains('\n')))
            throw new RemoteLaneTransportException("invalid-trx-name");
        return (code, names);
    }
    internal RemoteLaneResult Result(int code, string[] paths) => new(String("executorId"), String("lane"),
        String("filterHash"), String("commitSha"), String("treeSha"), String("mainSha"), String("manifestIdentity"), code, paths);
}
