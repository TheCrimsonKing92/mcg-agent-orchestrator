using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerResultSubmissionIngestion
{
    internal static bool TryIngest(string worktreeRoot, GoalId goalId, TaskSpec task,
        DateTimeOffset dispatchedAt, string standardOutputPath, out string diagnostic)
    {
        diagnostic = string.Empty;
        var path = WorkerResultSubmission.PathFor(worktreeRoot, goalId);
        if (!File.Exists(path)) return false;
        string? reason = null;
        string? block = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!ReadString(root, "role", out var role) || !ReadString(root, "task_id", out var taskId) ||
                !ReadString(root, "submitted_at_utc", out var timestamp) || !ReadString(root, "canonical_block", out block) ||
                !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var submittedAt))
                reason = "unreadable";
            else if (role != "Tester" || task.RequiredRole != AgentRole.Tester) reason = "role-mismatch";
            else if (!string.Equals(taskId, task.Id.Value, StringComparison.Ordinal)) reason = "task-mismatch";
            else if (submittedAt < dispatchedAt) reason = "stale";
            else
            {
                var assessment = WorkerResultBlockers.AssessTerminalVerificationOutput(AgentRole.Tester, block);
                if (assessment.Status != VerificationTerminalContractStatus.Valid) reason = $"invalid: {assessment.Diagnostic}";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            reason = "unreadable";
        }
        if (reason is null && !TryAppend(standardOutputPath, path, block!)) reason = "append-failed";
        diagnostic = reason is null
            ? $"worker_result_submission=ingested; path={path}"
            : $"worker_result_submission=ignored; reason={reason}; path={path}";
        return reason is null;
    }

    private static bool ReadString(JsonElement root, string key, out string? value)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return value is not null;
    }

    private static bool TryAppend(string stdoutPath, string submissionPath, string block)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(block))).ToLowerInvariant();
        var marker = $"[orchestrator ingested worker result submission {submissionPath} sha256={hash}]";
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                var tail = ReadTail(stdoutPath);
                if (tail.Split('\n').Any(line => string.Equals(line.TrimEnd('\r'), marker, StringComparison.Ordinal)))
                    return true;
                var prefix = tail.Length > 0 && !tail.EndsWith('\n') && !tail.EndsWith('\r') ? "\n" : string.Empty;
                var bytes = new UTF8Encoding(false).GetBytes(prefix + marker + "\n" + block + "\n");
                using var stream = new FileStream(stdoutPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                if (attempt == 4 || error is not IOException) return false;
                Thread.Sleep(20 * attempt);
            }
        }
        return false;
    }

    private static string ReadTail(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var atBeginning = stream.Length <= 64 * 1024;
        if (!atBeginning) stream.Seek(-64 * 1024, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: atBeginning);
        return reader.ReadToEnd();
    }
}
