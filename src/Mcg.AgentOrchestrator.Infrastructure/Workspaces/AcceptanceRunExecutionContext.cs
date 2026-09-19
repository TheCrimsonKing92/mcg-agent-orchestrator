using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IAcceptanceAttemptExecutionOwner : IAsyncDisposable { }

public interface IAcceptanceFocusedVerificationOwner : IAsyncDisposable { }

internal interface IAcceptanceRunExecutionContext : IAsyncDisposable
{
    string RunId { get; }
    string ResultsPrefix { get; }
    string ApparatusReceiptPath { get; }
    AcceptanceGateEngineSettings Settings { get; }
    CancellationToken CancellationToken { get; }
    AcceptanceAttemptArtifactCustodyContext? ArtifactCustody { get; }
    AcceptanceInvocationContext CreateInvocation(string name);
    T TrackStarted<T>(T task) where T : Task;
    void Cancel();
    void TrackEnvironment(DotnetBuildEnvironment environment);
    void MarkSuccessful();
    void ReportProgress(AcceptanceGateProgress progress);
    Func<bool>? ResolveCancellationProbe(bool boundary);
    void ApplyApparatusReceiptEnvironment(IDictionary<string, string?> environment);
    Task DrainAsync(CancellationToken cancellationToken = default);
}

internal sealed record AcceptanceAttemptIdentity(
    string AttemptId,
    string GoalId,
    string WorktreePath,
    string CandidateTreeSha,
    string MainSha,
    string VerifyingCommitSha,
    string ResultsPrefix,
    int? SlotIndex,
    int OwnerProcessId,
    string? LivenessCheckHint);

internal sealed record AcceptanceFocusedVerificationIdentity(
    string FocusedRunId,
    string GoalId,
    string WorktreePath,
    string CandidateSha,
    string ResultsPrefix,
    int? SlotIndex);

internal sealed record AcceptanceInvocationContext(
    string InvocationId,
    string Name,
    int Ordinal);

internal sealed record AcceptanceAttemptIdentityResolvers(
    Func<string, string?> ResolveCandidateTreeSha,
    Func<string, string?> ResolveMainSha,
    Func<string, string?> ResolveVerifyingCommitSha);

public sealed record AcceptanceRunExecutionOptions(
    Action<AcceptanceGateProgress>? ProgressSink = null,
    Func<bool>? CancellationProbe = null,
    Func<bool>? BoundaryCancellationProbe = null,
    TimeSpan? DrainTimeout = null,
    string? RunId = null,
    string? ResultsPrefix = null,
    string? LivenessCheckHint = null);

internal sealed class AcceptanceExecutionDrainException(string message) : InvalidOperationException(message);

internal sealed class AcceptanceExecutionIdentityChangedException(string message) : InvalidOperationException(message);

internal abstract class AcceptanceRunExecutionOwner : IAcceptanceRunExecutionContext
{
    private readonly ConcurrentDictionary<long, Task> _started = new();
    private readonly ConcurrentDictionary<string, int> _invocationOrdinals =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DotnetBuildEnvironment> _environments =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime;
    private readonly AcceptanceRunExecutionOptions _options;
    private long _nextChildId;
    private int _successful;
    private int _disposed;
    private int _resourcesReleased;

    protected AcceptanceRunExecutionOwner(
        string runId,
        string resultsPrefix,
        AcceptanceGateEngineSettings settings,
        CancellationToken cancellationToken,
        AcceptanceRunExecutionOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultsPrefix);
        RunId = runId;
        ResultsPrefix = Path.GetFullPath(resultsPrefix);
        ApparatusReceiptPath = TempRootApparatusLossReceiptStore.ResolvePath(ResultsPrefix)
            ?? throw new InvalidOperationException($"Acceptance run '{runId}' has no apparatus receipt path.");
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _options = options ?? new AcceptanceRunExecutionOptions();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public string RunId { get; }
    public string ResultsPrefix { get; }
    public string ApparatusReceiptPath { get; }
    public AcceptanceGateEngineSettings Settings { get; }
    public CancellationToken CancellationToken => _lifetime.Token;
    public AcceptanceAttemptArtifactCustodyContext? ArtifactCustody { get; protected init; }

    public AcceptanceInvocationContext CreateInvocation(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfDisposed();
        var ordinal = _invocationOrdinals.AddOrUpdate(
            name,
            addValue: 0,
            static (_, current) => checked(current + 1));
        return new AcceptanceInvocationContext(
            $"{RunId}:{name}:{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            name,
            ordinal);
    }

    public T TrackStarted<T>(T task) where T : Task
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();
        var childId = Interlocked.Increment(ref _nextChildId);
        if (!_started.TryAdd(childId, task))
        {
            throw new InvalidOperationException($"Acceptance run '{RunId}' could not register child '{childId}'.");
        }

        _ = task.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                _started.TryRemove(childId, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    public void Cancel() => _lifetime.Cancel();

    public void TrackEnvironment(DotnetBuildEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ThrowIfDisposed();
        if (environment.LeaseMetadataPath is null)
        {
            _environments.TryAdd(environment.RootPath, environment);
        }
    }

    public void MarkSuccessful() => Interlocked.Exchange(ref _successful, 1);

    public void ReportProgress(AcceptanceGateProgress progress) => _options.ProgressSink?.Invoke(progress);

    public Func<bool>? ResolveCancellationProbe(bool boundary) =>
        boundary ? _options.BoundaryCancellationProbe : _options.CancellationProbe;

    public void ApplyApparatusReceiptEnvironment(IDictionary<string, string?> environment) =>
        TempRootApparatusLossReceiptStore.ApplyScope(
            environment,
            RunId,
            ApparatusReceiptPath);

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        _lifetime.Cancel();
        var children = _started.ToArray();
        if (children.Length == 0)
        {
            return;
        }
        var tasks = children.Select(static child => child.Value).ToArray();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DrainTimeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var unresolved = children.Where(static child => !child.Value.IsCompleted).ToArray();
            if (unresolved.Length > 0)
            {
                throw new AcceptanceExecutionDrainException(
                    $"Acceptance run '{RunId}' teardown is on hold: {unresolved.Length} child task(s) remain unresolved " +
                    $"({string.Join(", ", unresolved.Select(child => $"child={child.Key}:task={child.Value.Id}:status={child.Value.Status}"))}; allocated={_nextChildId}).");
            }
        }
        catch
        {
            // Task.WhenAll observes every child fault. The execution path retains the authoritative
            // result; teardown must not replace it with a second interpretation of child failures.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var children = _started.Values.ToArray();
        try
        {
            await DrainAsync().ConfigureAwait(false);
            ReleaseResources();
        }
        catch (AcceptanceExecutionDrainException)
        {
            _ = ReleaseResourcesAfterChildrenCompleteAsync(children);
            throw;
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    private async Task ReleaseResourcesAfterChildrenCompleteAsync(Task[] children)
    {
        try { await Task.WhenAll(children).ConfigureAwait(false); }
        catch { /* every child fault is observed; the execution result remains authoritative */ }
        finally { ReleaseResources(); }
    }

    private void ReleaseResources()
    {
        if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0)
            return;
        if (Volatile.Read(ref _successful) != 0)
            foreach (var environment in _environments.Values)
                DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(environment);
        _lifetime.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

}

internal sealed class AcceptanceAttemptExecutionOwner : AcceptanceRunExecutionOwner, IAcceptanceAttemptExecutionOwner
{
    private readonly AcceptanceAttemptIdentityResolvers _identityResolvers;

    internal AcceptanceAttemptExecutionOwner(
        AcceptanceAttemptIdentity identity,
        AcceptanceGateEngineSettings settings,
        CancellationToken cancellationToken = default,
        AcceptanceRunExecutionOptions? options = null,
        AcceptanceAttemptIdentityResolvers? identityResolvers = null,
        bool publishResultsPrefix = true)
        : base(identity.AttemptId, identity.ResultsPrefix, settings, cancellationToken, options)
    {
        Identity = identity;
        _identityResolvers = identityResolvers ?? new AcceptanceAttemptIdentityResolvers(
            _ => identity.CandidateTreeSha,
            _ => identity.MainSha,
            _ => identity.VerifyingCommitSha);
        ArtifactResultsPrefix = publishResultsPrefix ? identity.ResultsPrefix : null;
        ArtifactCustody = string.IsNullOrWhiteSpace(identity.LivenessCheckHint)
            ? null
            : new AcceptanceAttemptArtifactCustodyContext(
                identity.AttemptId,
                identity.LivenessCheckHint,
                identity.OwnerProcessId);
    }

    internal AcceptanceAttemptIdentity Identity { get; }
    internal string? ArtifactResultsPrefix { get; }

    internal async Task<AcceptanceVerificationResult> ExecuteAsync(
        GoalAcceptanceVerifier verifier,
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<string>? changedFiles,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease)
    {
        var scopedVerifier = new GoalAcceptanceVerifier(verifier, this);
        var result = await scopedVerifier.RunOwnedAcceptanceAsync(
            worktreePath,
            goalId,
            changedFiles,
            stableSlotIndex,
            stableSlotLease,
            CancellationToken).ConfigureAwait(false);
        if (Identity.CandidateTreeSha != "unavailable" &&
            Identity.MainSha != "unavailable" &&
            Identity.VerifyingCommitSha != "unavailable")
        {
            EnsureIdentityCurrent(
                _identityResolvers.ResolveCandidateTreeSha(worktreePath),
                _identityResolvers.ResolveMainSha(worktreePath),
                _identityResolvers.ResolveVerifyingCommitSha(worktreePath));
        }
        if (result.Passed)
        {
            MarkSuccessful();
        }

        return result;
    }

    internal async Task<(AcceptanceCheckResult Result, bool Retried)> RunInvocationForTestsAsync(
        GoalAcceptanceVerifier verifier,
        AcceptanceInvocationContext invocation,
        GoalAcceptanceVerifier.AcceptanceManifestCheck check,
        string worktreePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(check);
        return await verifier.RunInvocationForOwnerTestsAsync(
            this, invocation, check, worktreePath, cancellationToken).ConfigureAwait(false);
    }

    internal void EnsureIdentityCurrent(
        string? candidateTreeSha,
        string? mainSha,
        string? verifyingCommitSha)
    {
        if (string.IsNullOrWhiteSpace(candidateTreeSha) ||
            string.IsNullOrWhiteSpace(mainSha) ||
            string.IsNullOrWhiteSpace(verifyingCommitSha))
        {
            throw new AcceptanceExecutionIdentityChangedException(
                $"Acceptance run '{RunId}' identity is unresolved; the result must remain on hold.");
        }

        if (!Identity.CandidateTreeSha.Equals(candidateTreeSha, StringComparison.Ordinal) ||
            !Identity.MainSha.Equals(mainSha, StringComparison.Ordinal) ||
            !Identity.VerifyingCommitSha.Equals(verifyingCommitSha, StringComparison.Ordinal))
        {
            throw new AcceptanceExecutionIdentityChangedException(
                $"Acceptance run '{RunId}' identity changed while the invocation was active; the result is stale.");
        }
    }
}

internal sealed class AcceptanceFocusedVerificationOwner : AcceptanceRunExecutionOwner, IAcceptanceFocusedVerificationOwner
{
    internal AcceptanceFocusedVerificationOwner(
        AcceptanceFocusedVerificationIdentity identity,
        AcceptanceGateEngineSettings settings,
        CancellationToken cancellationToken = default,
        AcceptanceRunExecutionOptions? options = null)
        : base(identity.FocusedRunId, identity.ResultsPrefix, settings, cancellationToken, options)
    {
        Identity = identity;
    }

    internal AcceptanceFocusedVerificationIdentity Identity { get; }

    internal async Task<FocusedEvidenceRunResult> ExecuteAsync(
        GoalAcceptanceVerifier verifier,
        string worktreePath,
        GoalId? goalId,
        string request,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        bool runBaselineArm)
    {
        var scopedVerifier = new GoalAcceptanceVerifier(verifier, this);
        var result = await scopedVerifier.RunOwnedFocusedEvidenceAsync(
            worktreePath,
            goalId,
            request,
            stableSlotIndex,
            stableSlotLease,
            runBaselineArm,
            CancellationToken).ConfigureAwait(false);
        if (result.Passed)
        {
            MarkSuccessful();
        }

        return result;
    }
}

internal sealed class AcceptanceRunExecutionContextView(
    IAcceptanceRunExecutionContext owner,
    string resultsPrefix) : IAcceptanceRunExecutionContext
{
    public string RunId => owner.RunId;
    public string ResultsPrefix { get; } = Path.GetFullPath(resultsPrefix);
    public string ApparatusReceiptPath { get; } =
        TempRootApparatusLossReceiptStore.ResolvePath(resultsPrefix)
        ?? throw new InvalidOperationException($"Acceptance run '{owner.RunId}' has no apparatus receipt path.");
    public AcceptanceGateEngineSettings Settings => owner.Settings;
    public CancellationToken CancellationToken => owner.CancellationToken;
    public AcceptanceAttemptArtifactCustodyContext? ArtifactCustody => owner.ArtifactCustody;
    public AcceptanceInvocationContext CreateInvocation(string name) => owner.CreateInvocation(name);
    public T TrackStarted<T>(T task) where T : Task => owner.TrackStarted(task);
    public void Cancel() => owner.Cancel();
    public void TrackEnvironment(DotnetBuildEnvironment environment) => owner.TrackEnvironment(environment);
    public void MarkSuccessful() => owner.MarkSuccessful();
    public void ReportProgress(AcceptanceGateProgress progress) => owner.ReportProgress(progress);
    public Func<bool>? ResolveCancellationProbe(bool boundary) => owner.ResolveCancellationProbe(boundary);
    public void ApplyApparatusReceiptEnvironment(IDictionary<string, string?> environment) =>
        TempRootApparatusLossReceiptStore.ApplyScope(environment, RunId, ApparatusReceiptPath);
    public Task DrainAsync(CancellationToken cancellationToken = default) => owner.DrainAsync(cancellationToken);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class AcceptanceExecutionOwners
{
    public static IAcceptanceAttemptExecutionOwner CreateAttempt(
        string worktreePath,
        GoalId? goalId = null,
        int? stableSlotIndex = null,
        CancellationToken cancellationToken = default,
        AcceptanceRunExecutionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        options ??= new AcceptanceRunExecutionOptions();
        var resultsPrefix = options.ResultsPrefix ??
            GoalAcceptanceVerifier.ResolveOwnerResultsPrefix(worktreePath, goalId, "gate");
        var attemptId = options.RunId;
        if (string.IsNullOrWhiteSpace(attemptId))
        {
            attemptId = Path.GetFileName(
                resultsPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        var identityResolvers = CreateGitIdentityResolvers();
        return CreateAttemptCore(
            worktreePath,
            goalId,
            stableSlotIndex,
            cancellationToken,
            options,
            resultsPrefix,
            attemptId,
            AcceptanceGateEngineSettings.Load(worktreePath),
            identityResolvers);
    }

    internal static IAcceptanceAttemptExecutionOwner CreateAttemptForVerifierCompatibility(
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        CancellationToken cancellationToken,
        AcceptanceGateEngineSettings settings,
        AcceptanceAttemptIdentityResolvers identityResolvers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(identityResolvers);
        var inheritedResultsPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var publishResultsPrefix = !string.IsNullOrWhiteSpace(inheritedResultsPrefix);
        var resultsPrefix = publishResultsPrefix
            ? Path.GetFullPath(inheritedResultsPrefix!)
            : GoalAcceptanceVerifier.ResolveOwnerResultsPrefix(worktreePath, goalId, "gate");
        var options = new AcceptanceRunExecutionOptions(ResultsPrefix: resultsPrefix);
        var attemptId = Path.GetFileName(
            resultsPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return CreateAttemptCore(
            worktreePath,
            goalId,
            stableSlotIndex,
            cancellationToken,
            options,
            resultsPrefix,
            attemptId,
            settings,
            identityResolvers,
            publishResultsPrefix);
    }

    private static AcceptanceAttemptExecutionOwner CreateAttemptCore(
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        CancellationToken cancellationToken,
        AcceptanceRunExecutionOptions options,
        string resultsPrefix,
        string attemptId,
        AcceptanceGateEngineSettings settings,
        AcceptanceAttemptIdentityResolvers identityResolvers,
        bool publishResultsPrefix = true) =>
        new(
            new AcceptanceAttemptIdentity(
                attemptId,
                goalId?.Value ?? "operator",
                Path.GetFullPath(worktreePath),
                identityResolvers.ResolveCandidateTreeSha(worktreePath) ?? "unavailable",
                identityResolvers.ResolveMainSha(worktreePath) ?? "unavailable",
                identityResolvers.ResolveVerifyingCommitSha(worktreePath) ?? "unavailable",
                resultsPrefix,
                stableSlotIndex,
                Environment.ProcessId,
                options.LivenessCheckHint),
            settings,
            cancellationToken,
            options,
            identityResolvers,
            publishResultsPrefix);

    private static AcceptanceAttemptIdentityResolvers CreateGitIdentityResolvers() =>
        new(
            path => GoalAcceptanceVerifier.ResolveGitScalarForExecutionOwner(path, "rev-parse", "HEAD^{tree}"),
            path => GoalAcceptanceVerifier.ResolveGitScalarForExecutionOwner(path, "rev-parse", "main"),
            path => GoalAcceptanceVerifier.ResolveGitScalarForExecutionOwner(path, "rev-parse", "HEAD"));

    public static IAcceptanceFocusedVerificationOwner CreateFocusedVerification(
        string worktreePath,
        GoalId? goalId = null,
        int? stableSlotIndex = null,
        CancellationToken cancellationToken = default,
        AcceptanceRunExecutionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        options ??= new AcceptanceRunExecutionOptions();
        var resultsPrefix = options.ResultsPrefix ??
            GoalAcceptanceVerifier.ResolveOwnerResultsPrefix(worktreePath, goalId, "pre-review");
        var runId = options.RunId ?? Path.GetFileName(
            resultsPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new AcceptanceFocusedVerificationOwner(
            new AcceptanceFocusedVerificationIdentity(
                runId,
                goalId?.Value ?? "operator",
                Path.GetFullPath(worktreePath),
                GoalAcceptanceVerifier.ResolveGitScalarForExecutionOwner(worktreePath, "rev-parse", "HEAD") ?? "unavailable",
                resultsPrefix,
                stableSlotIndex),
            AcceptanceGateEngineSettings.Load(worktreePath),
            cancellationToken,
            options);
    }
}
