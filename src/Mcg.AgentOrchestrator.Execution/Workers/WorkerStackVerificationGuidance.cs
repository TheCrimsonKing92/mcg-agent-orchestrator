using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// The one place worker context files choose per-stack build and test text.
internal static class WorkerStackVerificationGuidance
{
    internal static IReadOnlyList<string> CurrentTaskSection(ProjectStack? stack)
    {
        if (stack == ProjectStack.Go)
        {
            return
            [
                string.Empty,
                "## Build/Test Verification",
                "- Run `go build ./...`, `go vet ./...` and `go test ./...` from the worktree root before WORKER_RESULT; use `go test ./<package> -run '<TestRegex>'` while iterating.",
                "- Report each result in WORKER_RESULT `tests`, for example `tests: pass - go build, go vet, go test ./... ok` or `tests: fail - <package>: <first failing test or build error>`.",
                "- Do not build or test with `dotnet` or the .NET worker build helper; the orchestrator's `Submit-WorkerResult.ps1` submission command is required and is not a project build.",
                "- The acceptance gate re-runs the Go checks and decides the verdict."
            ];
        }
        else if (stack is null)
        {
            return [string.Empty, "## Build/Test Verification", .. WorkerNeutralStackGuidance.BuildTestLines];
        }
        else
        {
            var buildInvocation = OrchestratorBuildEvidenceCheck.FormatScriptInvocation(OrchestratorBuildEvidenceCheck.ResolveScriptPath());
            return
            [
                string.Empty,
                "## Build/Test Verification",
                $"- Run `{buildInvocation} <project.csproj> [project.csproj...]` from your worktree root before writing WORKER_RESULT for every project whose sources you changed; this is the only sanctioned worker-side .NET build check.",
                "- Report the build result in WORKER_RESULT `tests`, for example `tests: pass - build: 0 errors (Invoke-WorkerBuildCheck)` or `tests: fail - <build error>`.",
                $"- Compiling every changed project is required through `{buildInvocation}`. Do not run raw `dotnet test`, `.\\scripts\\Invoke-IsolatedDotnet.ps1`, or any other worker-side test execution because tests belong to the acceptance gate.",
                "- If no .NET project sources changed, report the non-.NET verification you ran or `tests: not-run - no .NET project sources changed; orchestrator acceptance gate verifies via stable slots`."
            ];
        }
    }

    internal static IReadOnlyList<string> DeterministicVerificationSection(Toolchain toolchain, GoalId goalId)
    {
        var lines = new List<string> { string.Empty };
        if (toolchain == Toolchain.Dotnet)
        {
            var buildInvocation = OrchestratorBuildEvidenceCheck.FormatScriptInvocation(OrchestratorBuildEvidenceCheck.ResolveScriptPath());
            lines.Add("## Worker Build Check");
            lines.Add($"- Developer/Tester subscription workers must run `{buildInvocation} <project.csproj> [project.csproj...]` from your worktree root for every project whose sources they changed before writing WORKER_RESULT.");
            lines.Add($"- The helper performs build-only verification through isolated artifacts under `{DotnetBuildStorageLayout.GoalArtifactsPath(goalId)}`; it does not run tests or spawn testhost.");
            lines.Add("- Routine builds resolve completion-required owning projects from the pinned goal baseline plus current dirty source, union them with requested roots, and explicitly build the full displayed scope in one invocation. A later tests-only request replaces the receipt with fresh complete coverage; previous receipts supply no coverage.");
            lines.Add($"- Compiling every changed project is required through `{buildInvocation}`; subscription workers must not run raw `dotnet test`, `.\\scripts\\Invoke-IsolatedDotnet.ps1`, or any other worker-side test execution because tests belong to the acceptance gate.");
            lines.Add("- In WORKER_RESULT, report build evidence such as `tests: pass - build: 0 errors (Invoke-WorkerBuildCheck)` or `tests: fail - <build error>`.");
        }
        else if (toolchain == Toolchain.Go)
        {
            lines.Add("## Go Verification");
            lines.Add("- Use `go build ./...` to verify the project compiles.");
            lines.Add("- Use `go vet ./...` to check for suspicious code.");
            lines.Add("- Use `go test ./...` to run the full test suite.");
            lines.Add("- Use `go test ./path/to/package/...` for focused package tests.");
        }
        else if (toolchain == Toolchain.Node)
        {
            lines.Add("## Node.js Verification");
            lines.Add("- Use `npm test` (or the package.json test script) to run the test suite.");
            lines.Add("- Use `npm run build` to verify the project builds.");
            lines.Add("- Check package.json scripts for the project's test and lint commands.");
        }
        else if (toolchain == Toolchain.Python)
        {
            lines.Add("## Python Verification");
            lines.Add("- Use `python -m pytest` (or the project test command) to run tests.");
            lines.Add("- Check pyproject.toml or requirements.txt for test configuration.");
        }
        else
        {
            lines.Add("## Verification");
            lines.Add("- Run the project's standard build and test commands.");
            lines.Add("- Check the repository README or CI configuration for the correct commands.");
        }

        return lines;
    }

    internal static IReadOnlyList<string> DeveloperBuildOnlyBrokerLines()
    {
        var buildInvocation = OrchestratorBuildEvidenceCheck.FormatScriptInvocation(OrchestratorBuildEvidenceCheck.ResolveScriptPath());
        return
        [
            "- build-only",
            "  Artifact: current-task.md",
            "  Use for: complete-scope build-only verification. Follow the required Invoke-WorkerBuildCheck instructions in current-task.md for every project whose sources you changed before writing WORKER_RESULT.",
            $"  Suggested command shape: `{buildInvocation} <project.csproj> [project.csproj...]`",
            "  Evidence: Report the build result in WORKER_RESULT and name deferred test classes for the conductor.",
            "  Verification ownership: Automated test execution and formal verification evidence for build-test-selection, static-policy-checks, and acceptance-evidence belong to the conductor verification/Reviewer pipeline. Do not execute tests or retrieve role-forbidden artifacts.",
            "  Failure handling: missing, stale, or failed required build artifacts are blockers; include the command and exit evidence."
        ];
    }
}
