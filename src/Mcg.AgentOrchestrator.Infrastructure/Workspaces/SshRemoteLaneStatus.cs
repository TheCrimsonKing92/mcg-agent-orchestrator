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
    internal (int ExitCode, string[] Names) BuildCheckCompletion()
    {
        if (!Fields.TryGetProperty("exitCode", out var exit) || exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out var code))
            throw new RemoteLaneTransportException("missing-or-invalid-exitCode");
        if (!Fields.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new RemoteLaneTransportException("missing-or-invalid-files");
        var names = new List<string>();
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.String ||
                !Regex.IsMatch(file.GetString()!, "^[A-Za-z0-9._-]+\\.(txt|json)\\z", RegexOptions.CultureInvariant))
                throw new RemoteLaneTransportException("invalid-build-file-name");
            names.Add(file.GetString()!);
        }
        if (code == 0 && (!names.Contains("build-output.txt") || !names.Contains("worker-build-receipt.json")))
            throw new RemoteLaneTransportException("missing-build-check-files exitCode=0");
        return (code, names.ToArray());
    }
    internal RemoteLaneResult BuildCheckResult(int code, string[] paths) => Result(code, []) with
    {
        BuildOutputPath = paths.FirstOrDefault(path => Path.GetFileName(path) == "build-output.txt"),
        BuildReceiptPath = paths.FirstOrDefault(path => Path.GetFileName(path) == "worker-build-receipt.json")
    };
    internal RemoteLaneResult Result(int code, string[] paths) => new(String("executorId"), String("lane"),
        String("filterHash"), String("commitSha"), String("treeSha"), String("mainSha"), String("manifestIdentity"), code, paths);
}
