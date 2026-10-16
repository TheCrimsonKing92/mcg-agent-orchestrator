using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerResultSubmissionVerdict(bool Accepted, string CanonicalBlock, IReadOnlyList<string> Errors);

public static class WorkerResultSubmission
{
    public const string FileName = "worker-result-submission.json";
    private static readonly string[] FieldOrder =
        ["files", "commands", "tests", "commit", "blockers", "findings", "touched_anchors", "model_fit", "skills", "confidence"];

    public static string PathFor(string worktreeRoot, GoalId goalId) =>
        Path.Combine(worktreeRoot, ".orchestrator-context", goalId.Value, FileName);

    public static WorkerResultSubmissionVerdict Validate(AgentRole role, string json, string worktreeRoot)
    {
        var errors = new List<string>();
        if (role != AgentRole.Tester)
            errors.Add($"structured submission is not enabled for role {role}");
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            errors.Add("submission must be a JSON object");
            return new(false, string.Empty, errors);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("submission must be a JSON object");
                return new(false, string.Empty, errors);
            }
            var allowed = AgentOutputDirectives.RequiredWorkerResultFieldNamesForRole(AgentRole.Tester)
                .Concat(["commit", "findings", "touched_anchors"]).ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = property.Name;
                if (!seen.Add(key)) errors.Add($"duplicate field: {key}");
                if (!allowed.Contains(key))
                {
                    errors.Add($"unknown field: {key}");
                    continue;
                }
                if (key is "findings" or "touched_anchors")
                {
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        errors.Add($"{key} must be a JSON array");
                    else
                        values[key] = JsonSerializer.Serialize(property.Value);
                    continue;
                }
                if (property.Value.ValueKind == JsonValueKind.Null) continue;
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"{key} must be a JSON string");
                    continue;
                }
                var value = property.Value.GetString()!;
                if (value.Contains('\r') || value.Contains('\n'))
                    errors.Add($"{key} must be a single line");
                else
                    values[key] = value.Trim();
            }
            values.TryAdd("files", "none");
            values.TryAdd("commit", "none");
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name == "findings" && property.Value.ValueKind == JsonValueKind.Array)
                    ValidateProjects(property.Value, worktreeRoot, errors);
            var block = string.Join('\n', new[] { "WORKER_RESULT:" }
                .Concat(FieldOrder.Where(values.ContainsKey).Select(key => $"{key}: {values[key]}"))
                .Append("END_WORKER_RESULT"));
            var assessment = WorkerResultBlockers.AssessTerminalVerificationOutput(AgentRole.Tester, block);
            if (assessment.Status != VerificationTerminalContractStatus.Valid)
                errors.Add(assessment.Diagnostic);
            return new(errors.Count == 0, block, errors);
        }
    }

    private static void ValidateProjects(JsonElement findings, string root, List<string> errors)
    {
        var findingIndex = 0;
        foreach (var finding in findings.EnumerateArray())
        {
            if (finding.ValueKind == JsonValueKind.Object &&
                finding.TryGetProperty("evidence_request", out var request) && request.ValueKind == JsonValueKind.Object &&
                request.TryGetProperty("selections", out var selections) && selections.ValueKind == JsonValueKind.Array)
            {
                var selectionIndex = 0;
                foreach (var selection in selections.EnumerateArray())
                {
                    if (selection.ValueKind == JsonValueKind.Object &&
                        selection.TryGetProperty("test_project", out var project) && project.ValueKind == JsonValueKind.String &&
                        project.GetString() is { } value && value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                        !ProjectExistsUnder(root, value))
                        errors.Add($"findings[{findingIndex}].evidence_request.selections[{selectionIndex}].test_project not found: {value}");
                    selectionIndex++;
                }
            }
            findingIndex++;
        }
    }

    private static bool ProjectExistsUnder(string root, string value)
    {
        try
        {
            var relative = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative)) return false;
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
            return fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && File.Exists(fullPath);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static int RunSubmitCommand(string[] args, TextWriter output, TextWriter error, Func<DateTimeOffset> utcNow)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new[] { "--worktree", "--task", "--path", "--input" };
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !keys.Contains(args[index]) || string.IsNullOrWhiteSpace(args[index + 1]) ||
                !options.TryAdd(args[index], args[index + 1]))
                return Usage(error);
        }
        if (options.Count != keys.Length) return Usage(error);
        string json;
        try { json = File.ReadAllText(options["--input"]); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"REJECTED: input file not readable: {options["--input"]}");
            return 2;
        }
        var verdict = Validate(AgentRole.Tester, json, options["--worktree"]);
        if (!verdict.Accepted)
        {
            foreach (var message in verdict.Errors) error.WriteLine($"REJECTED: {message}");
            return 1;
        }
        string? temporaryPath = null;
        try
        {
            var path = Path.GetFullPath(options["--path"]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new
            {
                role = "Tester",
                task_id = options["--task"],
                submitted_at_utc = utcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                canonical_block = verdict.CanonicalBlock
            }));
            File.Move(temporaryPath, path, overwrite: true);
            output.WriteLine($"ACCEPTED {options["--path"]}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"REJECTED: submission file not writable: {options["--path"]}");
            return 2;
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("REJECTED: usage: --worktree <root> --task <task id> --path <submission path> --input <json file>");
        return 2;
    }

    internal static string? ResolveScriptPath()
    {
        var home = Environment.GetEnvironmentVariable(OrchestratorBuildEvidenceCheck.HomeEnvironmentVariable)?.Trim();
        return string.IsNullOrEmpty(home) ? null : Path.GetFullPath(Path.Combine(home, "scripts", "Submit-WorkerResult.ps1"));
    }

    public static string FormatCommand(string? scriptPath, string worktreeRoot, TaskId taskId, string submissionPath)
    {
        var invocation = scriptPath is null
            ? $"[Submit-WorkerResult.ps1 unavailable: orchestrator home unresolved ({OrchestratorBuildEvidenceCheck.HomeEnvironmentVariable} unset)]"
            : $"& '{Quote(scriptPath)}'";
        return $"{invocation} -Worktree '{Quote(worktreeRoot)}' -Task {taskId.Value} -Path '{Quote(submissionPath)}' -InputFile <your-json-file>";
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
