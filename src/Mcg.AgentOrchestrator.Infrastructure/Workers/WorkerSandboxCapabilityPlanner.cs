using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerSandboxCapabilityResult(bool Allowed, string Status, string Detail);

public static class WorkerSandboxCapabilityPlanner
{
    public static WorkerSandboxCapabilityResult Evaluate(
        Goal goal,
        TaskSpec task,
        WorkerProfile profile,
        string workingDirectory,
        bool allowGitReference = false,
        WorkerSandboxOptions? sandboxOptions = null)
    {
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester))
        {
            return new WorkerSandboxCapabilityResult(true, "read-only", $"{task.RequiredRole} tasks run with read-only/plan sandbox settings.");
        }

        if (!File.Exists(Path.Combine(workingDirectory, ".git")))
        {
            return new WorkerSandboxCapabilityResult(false, "blocked", $"A goal workspace is required for {task.RequiredRole} file work.");
        }

        var provider = WorkerProviderCatalog.Default().ResolveProfile(profile.Name);
        var targetRisk = DetectTargetRisk(
            goal,
            task,
            profile,
            provider,
            allowGitReference,
            sandboxOptions ?? WorkerSandboxOptions.FromEnvironment());
        if (targetRisk is not null)
        {
            return targetRisk;
        }

        var patchCapability = WorkerProfileDiagnostics.EvaluatePatchCapability(profile, provider);
        return patchCapability.IsPatchCapable
            ? new WorkerSandboxCapabilityResult(true, "workspace-write", patchCapability.Detail)
            : new WorkerSandboxCapabilityResult(false, "blocked", patchCapability.Detail);
    }

    private static WorkerSandboxCapabilityResult? DetectTargetRisk(
        Goal goal,
        TaskSpec task,
        WorkerProfile profile,
        IWorkerProvider provider,
        bool allowGitReference,
        WorkerSandboxOptions sandboxOptions)
    {
        var text = $"{goal.Objective}\n{task.Description}\n{task.VerificationPlan}".ToLowerInvariant();
        if (text.Contains(".agents/skills", StringComparison.Ordinal) ||
            text.Contains(".agents\\skills", StringComparison.Ordinal))
        {
            if (CanWriteRepoScopedSkillTarget(profile, provider, sandboxOptions, out var detail))
            {
                return new WorkerSandboxCapabilityResult(
                    true,
                    "repo-skill-write",
                    detail);
            }

            return new WorkerSandboxCapabilityResult(
                false,
                "blocked",
                "Task appears to target repo-scoped .agents/skills files, but this profile lacks an authorized patch-capable, OS-confined worktree mode; Git metadata and commit authority remain with the orchestrator.");
        }

        if (text.Contains("skill.md", StringComparison.Ordinal))
        {
            return new WorkerSandboxCapabilityResult(
                false,
                "blocked",
                "Task appears to target a SKILL.md file; include the exact .agents/skills path and use a full-permission profile if this is intentional.");
        }

        // The .git block is a conservative guard against a worker trying to write the git object DB.
        // It false-positives on tasks that merely reference .git read-only (e.g. resolving the repo
        // root). allowGitReference is the operator's vetted override for exactly that case.
        if (!allowGitReference && ContainsGitDirectoryReference(text))
        {
            return new WorkerSandboxCapabilityResult(
                false,
                "blocked",
                "Task appears to target .git internals, which are outside the worker writable sandbox.");
        }

        return null;
    }

    // Matches ".git" only when it is a directory path segment (.git/ or .git\) or a
    // whole-word reference (not followed by a letter/digit). This avoids false-positives
    // on legitimate filenames such as .gitignore and .gitattributes.
    private static bool ContainsGitDirectoryReference(string text)
    {
        var idx = 0;
        while ((idx = text.IndexOf(".git", idx, StringComparison.Ordinal)) >= 0)
        {
            var after = idx + 4;
            if (after >= text.Length || !char.IsLetterOrDigit(text[after]))
                return true;
            idx = after;
        }
        return false;
    }

    private static bool CanWriteRepoScopedSkillTarget(
        WorkerProfile profile,
        IWorkerProvider provider,
        WorkerSandboxOptions sandboxOptions,
        out string detail)
    {
        var commandTemplate = profile.CommandTemplate;
        var claudeCompatible = (provider.Identity.Kind is ProviderKind.Unknown or ProviderKind.AnthropicClaudeCli) &&
            (commandTemplate.Contains("--permission-mode bypassPermissions", StringComparison.OrdinalIgnoreCase) ||
             commandTemplate.Contains("--permission-mode {permissionMode}", StringComparison.OrdinalIgnoreCase) ||
             commandTemplate.Contains("--dangerously-skip-permissions", StringComparison.OrdinalIgnoreCase));
        detail = claudeCompatible
            ? "Task targets repo-scoped .agents/skills files; profile uses the existing full-permission Claude-compatible worker mode."
            : string.Empty;
        return claudeCompatible;
    }
}
