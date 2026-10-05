using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerSkillTranscriptReader(
    IReadOnlyList<string> claudeProjectsRoots, Func<string, Stream>? openRead = null)
{
    public static WorkerSkillTranscriptReader CreateDefault() => new([
        Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { } config &&
            !string.IsNullOrWhiteSpace(config) ? config :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "projects")]);

    public IReadOnlyList<string>? Read(TaskDispatchRecord? dispatch, string stdoutPath)
    {
        try
        {
            var claude = dispatch?.WorkerProviderKind == ProviderKind.AnthropicClaudeCli;
            string? path;
            if (claude) path = FindClaudeTranscript(dispatch!);
            else if (dispatch?.WorkerProviderKind is ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark)
                path = File.Exists(stdoutPath + ".jsonl") ? stdoutPath + ".jsonl" : null;
            else return null;
            if (path is null) return null;
            using var stream = (openRead ?? OpenSharedRead)(path);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return (claude ? WorkerSkillReadParser.ParseClaudeTranscript(text) :
                WorkerSkillReadParser.ParseCodexEvents(text)).Skills;
        }
        catch (Exception) { return null; } // Optional telemetry must not change the refresh verdict.
    }

    private string? FindClaudeTranscript(TaskDispatchRecord dispatch)
    {
        var session = dispatch.ProviderSessionId;
        if (string.IsNullOrWhiteSpace(session) || session.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            session.IndexOfAny(['*', '?']) >= 0 || session.Contains("..", StringComparison.Ordinal)) return null;
        var roots = claudeProjectsRoots.Concat(dispatch.SandboxLowIntegrity
            ? [Path.Combine(dispatch.WorkingDirectory, ".mcg-sandbox")] : Array.Empty<string>());
        var fileName = session + ".jsonl";
        string? found = null;
        var latest = DateTime.MinValue;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var candidate in Directory.EnumerateFiles(root, fileName,
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                var modified = File.GetLastWriteTimeUtc(candidate);
                if (Path.GetFileName(candidate).Equals(fileName, StringComparison.Ordinal) &&
                    (found is null || modified > latest)) { found = candidate; latest = modified; }
            }
        }
        return found;
    }

    private static Stream OpenSharedRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
