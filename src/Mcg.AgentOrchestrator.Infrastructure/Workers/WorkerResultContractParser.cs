using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerResultContractParser
{
    internal void AddWorkerResultContractFindings(List<string> lines, TaskSpec task, TaskVerificationRecord verification)
    {
        if (!TryParseWorkerResultContract(verification, out var fields))
        {
            lines.Add($"- warn: prior {task.RequiredRole} task {task.Id.Value} has no WORKER_RESULT contract.");
            return;
        }

        lines.Add($"- ok: prior {task.RequiredRole} task {task.Id.Value} reported WORKER_RESULT contract.");
        if (!fields.TryGetValue("files", out var files) || string.IsNullOrWhiteSpace(files))
        {
            lines.Add($"- warn: prior {task.RequiredRole} task {task.Id.Value} contract has no files field.");
        }
        else
        {
            var generated = SplitContractList(files).Where(IsGeneratedPath).ToArray();
            if (generated.Length > 0)
            {
                lines.Add($"- fail: prior {task.RequiredRole} task {task.Id.Value} reported generated path changes: {string.Join(", ", generated)}.");
            }
        }

        if (!fields.TryGetValue("tests", out var tests) ||
            string.IsNullOrWhiteSpace(tests) ||
            tests.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"- warn: prior {task.RequiredRole} task {task.Id.Value} contract has no test evidence.");
        }

        if (!fields.TryGetValue("blockers", out var blockers) ||
            !blockers.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"- fail: prior {task.RequiredRole} task {task.Id.Value} reported blockers: {blockers}.");
        }

        if (!fields.TryGetValue("skills", out var skills) ||
            string.IsNullOrWhiteSpace(skills) ||
            skills.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"- warn: prior {task.RequiredRole} task {task.Id.Value} did not report skill usage.");
        }
    }

    internal bool TryParseWorkerResultContract(TaskVerificationRecord verification, out Dictionary<string, string> fields)
    {
        var text = $"{verification.StandardOutput}\n{verification.StandardError}";
        return WorkerResultParser.TryParseFields(text, out fields, out _);
    }

    private static string[] SplitContractList(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsGeneratedPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".scratch/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".orchestrator-prototype/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("TestResults/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/TestResults/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("playwright-report/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/playwright-report/", StringComparison.OrdinalIgnoreCase);
    }
}
