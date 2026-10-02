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
        WorkerSandboxOptions? sandboxOptions = null,
        Func<string, bool>? commandExists = null)
    {
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester))
        {
            return new WorkerSandboxCapabilityResult(true, "read-only", $"{task.RequiredRole} tasks run with read-only worker settings.");
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
            sandboxOptions ?? WorkerSandboxOptions.FromEnvironment(),
            commandExists);
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
        WorkerSandboxOptions sandboxOptions,
        Func<string, bool>? commandExists)
    {
        var text = $"{goal.Objective}\n{task.Description}\n{task.VerificationPlan}".ToLowerInvariant();
        var targetsRepoScopedSkill = WorkerTargetTextRules.TargetsRepoScopedSkill(text);
        // Git metadata is always conductor-owned. Evaluate this before any positive target-specific
        // capability result so a combined repo-skill/.git request cannot bypass even a vetted
        // read-only Git-reference override.
        if ((!allowGitReference || targetsRepoScopedSkill) && WorkerTargetTextRules.ContainsGitDirectoryReference(text))
        {
            return new WorkerSandboxCapabilityResult(
                false,
                "blocked",
                "Task appears to target .git internals, which are outside the worker writable sandbox.");
        }

        if (targetsRepoScopedSkill)
        {
            if (CanWriteRepoScopedSkillTarget(profile, provider, sandboxOptions, commandExists, out var detail))
            {
                return new WorkerSandboxCapabilityResult(
                    true,
                    "repo-skill-write",
                    detail);
            }

            return new WorkerSandboxCapabilityResult(
                false,
                "blocked",
                detail);
        }

        if (WorkerTargetTextRules.FindUnscopedSkillDefinition(text) >= 0)
        {
            return new WorkerSandboxCapabilityResult(
                false,
                "blocked",
                "Task appears to target a SKILL.md file; include the exact .agents/skills path and use a full-permission profile if this is intentional.");
        }

        return null;
    }

    private static bool CanWriteRepoScopedSkillTarget(
        WorkerProfile profile,
        IWorkerProvider provider,
        WorkerSandboxOptions sandboxOptions,
        Func<string, bool>? commandExists,
        out string detail)
    {
        if (!sandboxOptions.Enabled)
        {
            detail = "Repository-scoped skill writes require the orchestrator OS worker sandbox.";
            return false;
        }

        if (provider.Identity.Kind is not (
            ProviderKind.AnthropicClaudeCli or
            ProviderKind.OpenAICodexCli or
            ProviderKind.OpenAICodexSpark or
            ProviderKind.OpenAICodexOssCli))
        {
            detail = "Repository-scoped skill writes require a typed Codex or Claude CLI provider.";
            return false;
        }

        var launcher = WorkerProfileDiagnostics.EvaluateRealLauncher(profile, provider, commandExists);
        if (!launcher.IsRealLauncher)
        {
            detail = launcher.Detail;
            return false;
        }

        var patchCapability = WorkerProfileDiagnostics.EvaluatePatchCapability(profile, provider);
        if (!patchCapability.IsPatchCapable)
        {
            detail = patchCapability.Detail;
            return false;
        }

        detail = $"Task targets repo-scoped .agents/skills files; the patch-capable {provider.Identity.Kind} launcher is confined by the orchestrator OS worker sandbox.";
        return true;
    }
}
