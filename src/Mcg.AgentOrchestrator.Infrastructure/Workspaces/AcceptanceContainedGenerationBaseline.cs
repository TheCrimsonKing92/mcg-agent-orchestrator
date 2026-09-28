namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class AcceptanceContainedGenerationBaseline : IDisposable
{
    private readonly string? _candidateWorktreePath;
    private readonly string? _baselineRoot;
    private readonly Func<string, string[], GitCli.GitResult>? _runGit;
    private bool _disposed;

    internal AcceptanceContainedGenerationBaseline(
        string? containedMainSha,
        string? observedMainSha,
        string? worktreePath,
        bool useObservedBaseline,
        string? unresolvedReason,
        string? candidateWorktreePath = null,
        string? baselineRoot = null,
        Func<string, string[], GitCli.GitResult>? runGit = null)
    {
        ContainedMainSha = containedMainSha;
        ObservedMainSha = observedMainSha;
        WorktreePath = worktreePath;
        UseObservedBaseline = useObservedBaseline;
        UnresolvedReason = unresolvedReason;
        _candidateWorktreePath = candidateWorktreePath;
        _baselineRoot = baselineRoot;
        _runGit = runGit;
    }

    internal string? ContainedMainSha { get; }
    internal string? ObservedMainSha { get; }
    internal string? WorktreePath { get; }
    internal bool UseObservedBaseline { get; }
    internal string? UnresolvedReason { get; private set; }
    internal AcceptanceStructuralCoverageBaseline? Baseline { get; private set; }
    internal bool IsResolved => string.IsNullOrWhiteSpace(UnresolvedReason);

    internal static AcceptanceContainedGenerationBaseline Resolve(
        string candidateWorktreePath,
        string ownerKey,
        Func<string, string[], string?> resolveGitText,
        Func<string, string[], GitCli.GitResult> runGit)
    {
        var containedMainSha = NormalizeGitSha(
            resolveGitText(candidateWorktreePath, ["merge-base", "HEAD", "main"]));
        var observedMainSha = NormalizeGitSha(
            resolveGitText(candidateWorktreePath, ["rev-parse", "main"]));
        if (string.IsNullOrWhiteSpace(containedMainSha))
        {
            return Unresolved(observedMainSha, "merge-base-unresolved");
        }

        if (!string.IsNullOrWhiteSpace(observedMainSha) &&
            containedMainSha.Equals(observedMainSha, StringComparison.OrdinalIgnoreCase))
        {
            return new AcceptanceContainedGenerationBaseline(
                containedMainSha,
                observedMainSha,
                worktreePath: null,
                useObservedBaseline: true,
                unresolvedReason: null);
        }

        var baselineRoot = OrchestratorTempRoot.GetPurposeDirectory("structural-coverage-baselines");
        Directory.CreateDirectory(baselineRoot);
        var safeOwner = string.IsNullOrWhiteSpace(ownerKey) ? "operator" : ownerKey;
        var baselinePath = Path.Combine(
            baselineRoot,
            $"{safeOwner[..Math.Min(8, safeOwner.Length)]}-{Guid.NewGuid():N}");
        var add = runGit(
            candidateWorktreePath,
            ["worktree", "add", "--detach", baselinePath, containedMainSha]);
        if (!add.Succeeded)
        {
            TryDeleteDirectory(baselineRoot, baselinePath);
            return Unresolved(observedMainSha, "worktree-add-failed", containedMainSha);
        }

        return new AcceptanceContainedGenerationBaseline(
            containedMainSha,
            observedMainSha,
            baselinePath,
            useObservedBaseline: false,
            unresolvedReason: null,
            candidateWorktreePath,
            baselineRoot,
            runGit);
    }

    internal static async Task<AcceptanceContainedGenerationBaseline> PrepareAsync(
        string candidateWorktreePath,
        string ownerKey,
        Func<string, string, CancellationToken, Task<AcceptanceStructuralCoverageBaseline?>> prepareBaseline,
        Func<string, string[], string?> resolveGitText,
        Func<string, string[], GitCli.GitResult> runGit,
        CancellationToken cancellationToken)
    {
        var contained = Resolve(
            candidateWorktreePath,
            ownerKey,
            resolveGitText,
            runGit);
        if (!contained.IsResolved || contained.UseObservedBaseline)
        {
            return contained;
        }

        try
        {
            var prepared = await prepareBaseline(
                contained.WorktreePath!,
                contained.ContainedMainSha!,
                cancellationToken).ConfigureAwait(false);
            if (prepared is null)
            {
                contained.MarkUnresolved("project-missing");
            }
            else
            {
                contained.SetBaseline(prepared);
            }

            return contained;
        }
        catch (OperationCanceledException)
        {
            contained.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            contained.MarkUnresolved(ex is AcceptanceInfrastructureDeferredException deferred
                ? deferred.ReasonCode.Replace("trusted-main", "contained", StringComparison.Ordinal)
                : "baseline-preparation-failed");
            return contained;
        }
    }

    internal void SetBaseline(AcceptanceStructuralCoverageBaseline baseline) => Baseline = baseline;

    internal void MarkUnresolved(string reason)
    {
        if (string.IsNullOrWhiteSpace(UnresolvedReason))
        {
            UnresolvedReason = reason;
        }
    }

    internal static string? ResolveMainWorktreePath(
        string worktreePath,
        Func<string, string[], string?> resolveGitText)
    {
        var output = resolveGitText(worktreePath, ["worktree", "list", "--porcelain"]);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string? currentPath = null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                currentPath = line["worktree ".Length..].Trim();
            }
            else if (line.Equals("branch refs/heads/main", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(currentPath))
            {
                return currentPath;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_runGit is not null &&
            !string.IsNullOrWhiteSpace(_candidateWorktreePath) &&
            !string.IsNullOrWhiteSpace(WorktreePath))
        {
            _ = _runGit(
                _candidateWorktreePath,
                ["worktree", "remove", "--force", WorktreePath]);
        }

        if (!string.IsNullOrWhiteSpace(_baselineRoot) && !string.IsNullOrWhiteSpace(WorktreePath))
        {
            TryDeleteDirectory(_baselineRoot, WorktreePath);
        }
    }

    private static AcceptanceContainedGenerationBaseline Unresolved(
        string? observedMainSha,
        string reason,
        string? containedMainSha = null) =>
        new(
            containedMainSha,
            observedMainSha,
            worktreePath: null,
            useObservedBaseline: false,
            unresolvedReason: reason);

    private static string? NormalizeGitSha(string? output)
    {
        var sha = output?.Trim();
        return sha is { Length: >= 7 } && sha.All(Uri.IsHexDigit) ? sha : null;
    }

    private static void TryDeleteDirectory(string baselineRoot, string baselinePath)
    {
        try
        {
            var resolvedRoot = Path.GetFullPath(baselineRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedPath = Path.GetFullPath(baselinePath);
            if (resolvedPath.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedPath))
            {
                Directory.Delete(resolvedPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Git worktree removal is authoritative; partial-directory cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the coverage receipt when a stale handle delays temp cleanup.
        }
    }
}
