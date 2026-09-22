using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Native, cross-platform replacement for the old generated-PowerShell dispatch wrapper. Runs
/// detached as a hidden <c>__dispatch-run</c> subcommand of the App: it sets the build env, launches
/// the worker command through the resolved PowerShell host (<see cref="WorkerShell"/>), streams
/// stdout/stderr to log files, writes a periodic heartbeat, and always records the exit code so the
/// orchestrator can reconcile the dispatch. The detached process outlives the orchestrator CLI, so
/// this logic must be self-contained and never throw without writing the exit file.
/// </summary>
public static class DispatchProcessHost
{
    public const string SubcommandName = "__dispatch-run";
    public const string StartGatePathVariable = "MCG_DISPATCH_HOST_START_GATE";
    public const string WorkerDispatchKind = "worker";
    public const string PrepDispatchKind = "prep";
    internal const string LowIntegritySetupArtifactName = "low-integrity-setup.json";
    internal const string WorkerCaBundleFileName = "worker-ca-bundle.pem";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly IcaclsIntegrityLabeler IntegrityLabeler = new();

    // Dispatch supervision: an unbounded wait lets a hung worker — or a stuck grandchild such as a
    // git process wedged on an index.lock — hold the owned job open indefinitely, which blocks the
    // conductor loop and strands the goal (observed: a git child kept a dispatch alive 34 minutes,
    // jamming a --watch loop for its full budget). The watchdog enforces a hard max runtime and an
    // idle-stall cap (no stdout/stderr growth), then reaps the whole tree. Both are overridable via
    // env so an operator can widen them for an unusually long legitimate dispatch.
    private static readonly TimeSpan DefaultMaxRuntime = TimeSpan.FromMinutes(60);
    // Idle/stall cap: reap a worker that has streamed output but then made no progress -- no new output
    // AND no CPU growth (lastProgressAt updates on either) -- for this long. Cut 20m -> 15m to recover
    // faster from the known codex-CLI mid-session hang (openai/codex #7156/#7187): codex's reqwest client
    // sets no request/read timeout and its Request.timeout field is never applied, so a black-holed API
    // request hangs at ~0 CPU indefinitely. A working codex keeps lastProgressAt fresh via CPU growth, so
    // a 15m full stall is almost certainly the hang; the margin still covers a long, quiet build/test.
    private static readonly TimeSpan DefaultMaxIdle = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan WatchdogProbeInterval = TimeSpan.FromSeconds(15);
    private const long CpuProgressEpsilonMs = 50L;
    internal const int ProviderSessionCaptureByteLimit = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
    };

    public sealed record DispatchRunParameters(
        string Command,
        string WorkingDirectory,
        string StdoutPath,
        string StderrPath,
        string ExitCodePath,
        string? HeartbeatPath,
        bool DisableSharedCompilation,
        // OS worker sandbox: when SandboxLowIntegrity is set, the worker runs at LOW integrity (same
        // operator user) with a Low CODEX_HOME/TEMP. SandboxWorktreeWritable controls whether the
        // worktree is also Low (Developer/Tester) or remains Medium/read-only (other Codex roles).
        // The shared .git stays Medium and out of reach. Default = run at Medium integrity.
        bool SandboxLowIntegrity = false,
        WorkerSandboxProvider Provider = WorkerSandboxProvider.Unknown,
        string? PromptPath = null,
        bool SandboxWorktreeWritable = true,
        string? ProviderSessionId = null,
        string? WorktreeHeadSha = null,
        string? DirtyStateHash = null,
        string Kind = WorkerDispatchKind,
        string? PrepGoalId = null,
        string? PrepTaskId = null,
        string? PrepRecordPath = null,
        string? PrepHeartbeatPath = null,
        string? PrepExitCodePath = null,
        string? ChildExitRecordPath = null,
        string? HostDiagnosticPath = null,
        int HeartbeatIntervalMilliseconds = 15_000,
        IReadOnlyList<MandatoryContextFileDescriptor>? MandatoryContextFiles = null,
        ProcessOutputDrainPolicy? OutputDrainPolicy = null,
        string? SandboxInstanceName = null,
        // The Claude credential source the CONDUCTOR selected during dispatch preflight, transported to
        // this detached host so seeding consumes that decision instead of selecting again in a process
        // whose environment may differ. Path and source kind only - no credential material crosses here,
        // and the payload is re-read at seeding time. Null for non-Claude dispatches; a Claude sandbox
        // dispatch that arrives without it fails before launch rather than re-deriving a source.
        ClaudeCredentialSourceSelection? ClaudeCredentialSelection = null);

    public sealed record DispatchChildExitRecord(
        int ProcessId,
        int? ExitCode,
        DateTimeOffset RecordedAt,
        string State = "exited");

    public sealed record DispatchPrepRecord(
        string Kind,
        string GoalId,
        string TaskId,
        string WorkingDirectory,
        string HeartbeatPath,
        string ExitCodePath,
        DateTimeOffset StartedAt,
        WorkerSandboxProvider Provider,
        bool SandboxWorktreeWritable,
        DateTimeOffset? CompletedAt);

    public static string WriteParameters(string path, DispatchRunParameters parameters)
    {
        WriteAllTextDurable(path, JsonSerializer.Serialize(parameters, JsonOptions));
        return path;
    }

    internal static DispatchRunParameters ReadParameters(string path) =>
        JsonSerializer.Deserialize<DispatchRunParameters>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException("Dispatch parameters were empty.");

    public static string WritePrepRecord(string path, DispatchPrepRecord record)
    {
        WriteAllTextDurable(path, JsonSerializer.Serialize(record, JsonOptions));
        return path;
    }

    internal static IDisposable AcquireMandatoryContextFileLeases(DispatchRunParameters parameters)
    {
        if (parameters.MandatoryContextFiles is not { Count: > 0 })
        {
            return MandatoryContextFileLeaseSet.Empty;
        }

        var root = Path.GetFullPath(parameters.WorkingDirectory);
        var streams = new List<FileStream>();
        try
        {
            foreach (var descriptor in parameters.MandatoryContextFiles)
            {
                if (descriptor.ContractVersion != ContextContractVersion.V1.Value)
                {
                    throw new InvalidOperationException($"Mandatory context '{descriptor.LogicalIdentity}' has unsupported contract version {descriptor.ContractVersion}.");
                }

                if (!descriptor.RoleVisibility.Contains(descriptor.TargetRole))
                {
                    throw new InvalidOperationException(
                        $"Mandatory context '{descriptor.LogicalIdentity}' is not visible to target role {descriptor.TargetRole}.");
                }

                var relative = new LogicalArtifactIdentity(descriptor.RelativePath).Value;
                var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                var relativeToRoot = Path.GetRelativePath(root, fullPath);
                if (Path.IsPathRooted(relativeToRoot) || relativeToRoot == ".." ||
                    relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Mandatory context '{descriptor.LogicalIdentity}' resolves outside the dispatch worktree.");
                }


                for (FileSystemInfo? current = new FileInfo(fullPath); current is not null; current = current switch
                    {
                        FileInfo file => file.Directory,
                        DirectoryInfo directory => directory.Parent,
                        _ => null
                    })
                {
                    if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            $"Mandatory context '{descriptor.LogicalIdentity}' traverses a reparse point.");
                    }

                    if (string.Equals(
                        current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                        root.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }

                var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!actualHash.Equals(descriptor.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Mandatory context '{descriptor.LogicalIdentity}' hash mismatch: expected {descriptor.Sha256}, found {actualHash}.");
                }

                stream.Position = 0;
            }

            return new MandatoryContextFileLeaseSet(streams);
        }
        catch
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    internal static void PrependMandatoryContextAuthorityPreflight(
        ProcessStartInfo startInfo,
        DispatchRunParameters parameters)
    {
        if (parameters.MandatoryContextFiles is not { Count: > 0 })
        {
            return;
        }

        var lastIndex = startInfo.ArgumentList.Count - 1;
        if (lastIndex < 0)
        {
            throw new InvalidOperationException("Mandatory context authority preflight requires a worker command argument.");
        }

        var root = Path.GetFullPath(parameters.WorkingDirectory);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            parameters.MandatoryContextFiles.Select(descriptor => new
            {
                descriptor.LogicalIdentity,
                Path = Path.GetFullPath(Path.Combine(
                    root,
                    descriptor.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                descriptor.Sha256
            }),
            JsonOptions);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var manifestPath = Path.Combine(
            ResolveSandboxRoot(parameters),
            $"mandatory-context-authority-{manifestHash}.json");
        // The authority manifest exists on every platform, including those where the Windows
        // integrity sandbox is not applied. Keep that host-owned scratch out of worktree status
        // at its creation seam rather than relying on platform-specific sandbox preparation.
        ExcludeSandboxFromGit(parameters.WorkingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        WriteAllTextDurable(manifestPath, Encoding.UTF8.GetString(manifestBytes));
        var encodedManifestPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifestPath));
        var preflight =
            "$mcgContextManifestPath=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedManifestPath + "')); " +
            "$mcgContextManifestBytes=[IO.File]::ReadAllBytes($mcgContextManifestPath); " +
            "$mcgContextManifestSha=[Security.Cryptography.SHA256]::Create(); " +
            "$mcgContextManifestActual=-join ($mcgContextManifestSha.ComputeHash($mcgContextManifestBytes) | ForEach-Object { $_.ToString('x2') }); " +
            "$mcgContextManifestSha.Dispose(); " +
            "if (-not [String]::Equals($mcgContextManifestActual,'" + manifestHash + "',[StringComparison]::Ordinal)) { [Console]::Error.WriteLine('[dispatch-host] mandatory context authority manifest hash mismatch'); exit 86 }; " +
            "$mcgContextJson=[Text.Encoding]::UTF8.GetString($mcgContextManifestBytes); " +
            "$mcgContextItems=$mcgContextJson | ConvertFrom-Json; " +
            "foreach ($mcgContextItem in $mcgContextItems) { " +
            "$mcgContextStream=$null; $mcgContextSha=$null; try { " +
            "$mcgContextStream=[IO.File]::Open($mcgContextItem.Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read); " +
            "$mcgContextSha=[Security.Cryptography.SHA256]::Create(); " +
            "$mcgContextActual=-join ($mcgContextSha.ComputeHash($mcgContextStream) | ForEach-Object { $_.ToString('x2') }); " +
            "if (-not [String]::Equals($mcgContextActual,$mcgContextItem.Sha256,[StringComparison]::Ordinal)) { throw \"hash mismatch under launched worker authority\" } " +
            "} catch { [Console]::Error.WriteLine('[dispatch-host] mandatory context authority preflight failed for ' + $mcgContextItem.LogicalIdentity + ': ' + $_.Exception.Message); exit 86 } " +
            "finally { if ($null -ne $mcgContextSha) { $mcgContextSha.Dispose() }; if ($null -ne $mcgContextStream) { $mcgContextStream.Dispose() } } " +
            "}; ";
        startInfo.ArgumentList[lastIndex] = preflight + startInfo.ArgumentList[lastIndex];
    }

    private static void WriteAllTextDurable(string path, string payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.Write(payload);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void AppendAllTextDurable(string path, string payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.Write(payload);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    // OS worker sandbox via Mandatory Integrity Control. The worker runs at LOW integrity as the SAME
    // operator user — so the toolchain (node/codex) and codex auth are reachable (reads aren't
    // MIC-restricted) — but it can only WRITE Low-labeled objects (the worktree + a Low CODEX_HOME/TEMP),
    // never the medium-integrity profile or main repo. The host runs at medium and cannot launch a Low
    // child without privilege, so we prepend a self-drop wrapper to the worker command (a process may
    // lower its own integrity freely). Validated by
    // DispatchProcessHostTests.LowIntegritySetupKeepsLinkedWorktreeGitFileMedium.
    internal static WorkerSandboxPreparationResult ApplyWorkerSandbox(ProcessStartInfo startInfo, DispatchRunParameters parameters)
        => ApplyWorkerSandbox(startInfo, parameters, WorkerSandboxPreparer.CreateDefault());

    internal static WorkerSandboxPreparationResult ApplyWorkerSandbox(
        ProcessStartInfo startInfo,
        DispatchRunParameters parameters,
        WorkerSandboxPreparer preparer,
        Action<string, DateTimeOffset, TimeSpan>? recordStep = null,
        Action<string>? protectWorkspaceBoundary = null,
        Action<string>? protectGitMetadata = null,
        // Injected environment for the Claude path, so a sandbox test resolves nothing from process
        // state and never falls through to the operator's REAL credential store (which would make it
        // depend on host auth). It answers ANTHROPIC_API_KEY in every case; it selects a source ONLY
        // when the dispatch transported none, which in production is a hard failure, not a fallback -
        // see CreateClaudeCredentialResolver.
        Func<string, string?>? providerEnvironmentReader = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WorkerSandboxPreparationResult(false, false);
        }

        var sandboxRoot = ResolveSandboxRoot(parameters);
        if (!parameters.SandboxLowIntegrity)
        {
            ConfigurePowerShellModuleAnalysisCache(startInfo.Environment, sandboxRoot);
            ExcludeSandboxFromGit(parameters.WorkingDirectory);
            return new WorkerSandboxPreparationResult(false, false);
        }

        T Track<T>(string phase, Func<T> action)
        {
            var stepStartedAt = DateTimeOffset.UtcNow;
            try
            {
                return action();
            }
            finally
            {
                recordStep?.Invoke(phase, stepStartedAt, DateTimeOffset.UtcNow - stepStartedAt);
            }
        }

        void TrackAction(string phase, Action action)
        {
            var stepStartedAt = DateTimeOffset.UtcNow;
            try
            {
                action();
            }
            finally
            {
                recordStep?.Invoke(phase, stepStartedAt, DateTimeOffset.UtcNow - stepStartedAt);
            }
        }

        // Label ONLY the worktree Low so the Low worker can edit it. The shared git common dir is
        // deliberately left at medium integrity: it lives OUTSIDE the worktree (in the main repo's
        // .git/worktrees), so the worker must not be able to write it — that is the write-confinement
        // guarantee. The worker only EDITS the worktree; the orchestrator (medium) commits those edits
        // afterwards (BackgroundDispatchRunner.TryCommitWorktreeEdits). This also removes the slow,
        // broad per-dispatch icacls /T walk over the whole .git that labeling the common dir required.
        var preparation = Track("prepare-roots", () => parameters.SandboxWorktreeWritable
            ? preparer.Prepare(parameters.WorkingDirectory, sandboxRoot)
            : preparer.PrepareSandboxRootOnly(parameters.WorkingDirectory, sandboxRoot));
        if (preparation.RecoveryAction is { } recoveryAction)
        {
            throw new InvalidOperationException(recoveryAction.Reason);
        }

        protectWorkspaceBoundary ??= ProtectWorkspaceBoundary;
        protectGitMetadata ??= ProtectGitMetadata;

        var shouldProtectWorkspaceBoundary = parameters.SandboxWorktreeWritable &&
            !preparation.ReceiptCoversProtectionPhase(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase);
        var shouldProtectGitMetadata = parameters.SandboxWorktreeWritable &&
            !preparation.ReceiptCoversProtectionPhase(WorkerSandboxPreparer.ProtectGitMetadataPhase);
        var receiptFastPath = preparation.PrepReceiptHit &&
            !shouldProtectWorkspaceBoundary &&
            !shouldProtectGitMetadata;

        if (shouldProtectWorkspaceBoundary)
        {
            TrackAction(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase, () => protectWorkspaceBoundary(parameters.WorkingDirectory));
        }

        if (shouldProtectGitMetadata)
        {
            TrackAction(WorkerSandboxPreparer.ProtectGitMetadataPhase, () => protectGitMetadata(parameters.WorkingDirectory));
        }

        if (parameters.SandboxWorktreeWritable)
        {
            WorkerSandboxPreparer.WriteCompletedProtectionReceipts(parameters.WorkingDirectory, sandboxRoot);
        }

        var effectivePreparation = preparation with { PrepReceiptHit = receiptFastPath };
        if (receiptFastPath)
        {
            // The receipt verifies the prepared root identity, schema, Low inheritable integrity, and
            // explicit protection-phase coverage. Covered phases skip the medium-integrity icacls calls
            // that caused the fixed ~120s tax observed on receipt hits.
            recordStep?.Invoke("receipt-fast-path", DateTimeOffset.UtcNow, TimeSpan.Zero);
        }

        TrackAction("materialize-sandbox", () =>
        {
            // Per-dispatch Low-labeled writable set: provider-neutral temp scratch, command shims, and any
            // provider-specific home/config directories. The sandbox root is labeled before child paths are
            // materialized so they inherit Low without a second recursive icacls traversal.
            var tempDir = Path.Combine(sandboxRoot, "temp");
            var sandboxBin = CreateSandboxBinDirectory(sandboxRoot);
            Directory.CreateDirectory(tempDir);
            ConfigurePowerShellModuleAnalysisCache(startInfo.Environment, sandboxRoot);
            TrackAction("materialize-shims", () => WriteWorkerCommandShims(sandboxBin, startInfo.Environment["PATH"]));

            // The credential source this dispatch actually seeded, carried to the setup artifact so an
            // operator can read which login a worker was launched with. It is the resolved result
            // seeding consumed, not a re-derived guess about it.
            ClaudeCredentialResolution? seededCredentialSource = null;
            TrackAction("materialize-provider-seed", () => seededCredentialSource = SeedProviderEnvironment(
                startInfo,
                parameters.Provider,
                sandboxRoot,
                parameters.StderrPath,
                // The conductor's transported selection, rehydrated into the single resolver this host
                // seeds from. No candidate evaluation happens in this process.
                claudeCredentialResolver: CreateClaudeCredentialResolver(parameters, providerEnvironmentReader)));
            TrackAction("materialize-ca-bundle", () => SeedWorkerCaBundle(startInfo, sandboxRoot, parameters.StderrPath));

            // Keep the sandbox scratch out of git's view so it never registers as a dirty/untracked path:
            // the worktree must read as clean after the orchestrator commits the worker's real edits.
            TrackAction("materialize-git-exclude", () => ExcludeSandboxFromGit(parameters.WorkingDirectory));

            startInfo.Environment["TEMP"] = tempDir;
            startInfo.Environment["TMP"] = tempDir;
            // PowerShell otherwise derives this cache from inherited profile locations. Keep only
            // its disposable module-analysis cache inside the ignored worker sandbox; relocating
            // LOCALAPPDATA/APPDATA breaks unrelated per-user tool and PowerShell resolution.
            startInfo.Environment["PATH"] = BuildLowIntegrityPath(startInfo.Environment["PATH"], WorkerShell.Executable, sandboxBin);
            var dropScript = Path.Combine(sandboxRoot, "drop-to-low.ps1");
            TrackAction("materialize-artifacts", () =>
            {
                WriteLowIntegritySetupArtifact(
                    sandboxRoot,
                    parameters.WorkingDirectory,
                    effectivePreparation,
                    seededCredentialSource);

                // Prepend a self-drop-to-Low wrapper. ArgumentList is [BaseArgs..., Command]; replace Command
                // with ". 'drop.ps1'; <Command>" so the worker (and its children: codex/node) run Low.
                File.WriteAllText(dropScript, DropToLowScript);
            });
            var lastIndex = startInfo.ArgumentList.Count - 1;
            if (lastIndex >= 0)
            {
                startInfo.ArgumentList[lastIndex] = $". '{dropScript}'; {startInfo.ArgumentList[lastIndex]}";
            }
        });

        return effectivePreparation;
    }

    internal static string ResolveSandboxRoot(DispatchRunParameters parameters)
    {
        var sharedRoot = Path.GetFullPath(Path.Combine(parameters.WorkingDirectory, ".mcg-sandbox"));
        if (string.IsNullOrWhiteSpace(parameters.SandboxInstanceName))
        {
            return sharedRoot;
        }

        var instanceRoot = Path.GetFullPath(Path.Combine(sharedRoot, parameters.SandboxInstanceName));
        var requiredPrefix = sharedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!instanceRoot.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sandbox instance must resolve below the dispatch sandbox root.");
        }

        return instanceRoot;
    }

    internal static void PrepareSharedSandboxState(DispatchRunParameters parameters) =>
        ExcludeSandboxFromGit(parameters.WorkingDirectory);

    private static void ConfigurePowerShellModuleAnalysisCache(
        IDictionary<string, string?> environment,
        string sandboxRoot)
    {
        var powershellDirectory = Path.Combine(sandboxRoot, "powershell");
        Directory.CreateDirectory(powershellDirectory);
        environment["PSModuleAnalysisCachePath"] = Path.Combine(powershellDirectory, "ModuleAnalysisCache");
    }

    /// <summary>
    /// Runs the Claude credential preflight ONCE for a dispatch about to be launched, and returns the
    /// reported view whose <see cref="ClaudeCliAuthState.ToTransportedSelection"/> the conductor puts in
    /// <see cref="DispatchRunParameters.ClaudeCredentialSelection"/>. The detached host then seeds from
    /// that selection instead of resolving again (see
    /// <see cref="ClaudeCredentialResolver.ForTransportedSelection"/>), so the source reported here is
    /// the source the worker receives - derived from this state, never computed a second time.
    /// Returns null for non-Claude or non-sandbox dispatches, which seed no Claude login and must not
    /// touch a credential store at all.
    /// A rejected source is still reported and still transported on purpose: the host's pre-launch
    /// failure must name the source preflight named rather than fall back to a locally chosen one.
    /// </summary>
    internal static ClaudeCliAuthState? PreflightClaudeCredentialSource(
        WorkerSandboxProvider provider,
        bool sandboxLowIntegrity,
        ClaudeCredentialResolver? resolver = null,
        Func<string, string?>? environmentReader = null,
        Func<string?>? defaultHomeProvider = null) =>
        provider == WorkerSandboxProvider.Claude && sandboxLowIntegrity
            ? ClaudeCliAuthProbe.From(
                resolver ?? new ClaudeCredentialResolver(environmentReader, defaultHomeProvider))
            : null;

    /// <summary>
    /// The selection a dispatch start hands to the detached host. The conductor's dispatch preflight
    /// already chose it while preparing this dispatch and recorded it on the dispatch record, so the
    /// recorded decision is consumed VERBATIM here: no candidate is evaluated, no credential store is
    /// read, and the login the recorded preflight finding names is the login the host seeds.
    /// <paramref name="recordedSourceDirectory"/> is blank only for a dispatch that never ran a Claude
    /// auth preflight - one prepared before this handoff existed, or one whose worker sandbox was
    /// disabled when it was prepared and enabled by the time it started. Such a dispatch has no reported
    /// source to honor, so this boundary performs the single resolution itself rather than transporting
    /// nothing and failing a launch whose preflight never claimed a source.
    /// Returns null for non-Claude or non-sandbox dispatches, which seed no Claude login at all and must
    /// not touch a credential store.
    /// </summary>
    internal static ClaudeCredentialSourceSelection? TransportedClaudeCredentialSelection(
        string? recordedSourceDirectory,
        bool recordedSourceIsExplicit,
        WorkerSandboxProvider provider,
        bool sandboxLowIntegrity,
        Func<string, string?>? environmentReader = null,
        Func<string?>? defaultHomeProvider = null)
    {
        if (provider != WorkerSandboxProvider.Claude || !sandboxLowIntegrity)
        {
            return null;
        }

        return ClaudeCredentialSourceSelection.FromRecordedSelection(
                recordedSourceDirectory,
                recordedSourceIsExplicit)
            ?? PreflightClaudeCredentialSource(
                provider,
                sandboxLowIntegrity,
                environmentReader: environmentReader,
                defaultHomeProvider: defaultHomeProvider)
                ?.ToTransportedSelection();
    }

    /// <summary>
    /// Builds the ONE resolver this dispatch host uses for the Claude credential source. The conductor's
    /// transported selection wins whenever it is present; the injected environment reader is a test-only
    /// fallback for host tests that construct their own synthetic source. With neither, the resolver
    /// resolves to <see cref="ClaudeCredentialStatus.SelectionNotTransported"/>, so subscription seeding
    /// fails before launch instead of silently re-deriving a source in this process.
    /// </summary>
    internal static ClaudeCredentialResolver CreateClaudeCredentialResolver(
        DispatchRunParameters parameters,
        Func<string, string?>? providerEnvironmentReader = null) =>
        parameters.ClaudeCredentialSelection is null && providerEnvironmentReader is not null
            ? new ClaudeCredentialResolver(providerEnvironmentReader)
            : ClaudeCredentialResolver.ForTransportedSelection(
                parameters.ClaudeCredentialSelection,
                providerEnvironmentReader);

    /// <summary>
    /// Publishes the provider's sandbox environment onto <paramref name="startInfo"/>. For Claude
    /// subscription auth it returns the ONE resolved credential source that was seeded, so a caller
    /// can report which login the worker will use; every other provider and API-key mode return null.
    /// </summary>
    internal static ClaudeCredentialResolution? SeedProviderEnvironment(
        ProcessStartInfo startInfo,
        WorkerSandboxProvider provider,
        string sandboxRoot,
        string? stderrPath = null,
        Func<string?>? anthropicApiKeyAccessor = null,
        Func<string>? claudeCredentialDirectoryAccessor = null,
        Func<string, string?>? environmentReader = null,
        Func<string?>? defaultHomeProvider = null,
        // Transported-resolution seam: a caller that already resolved the Claude credential source
        // (auth preflight) passes its resolver so seeding consumes that ONE resolved result instead of
        // computing a second selection that could disagree. It takes precedence over the three
        // accessor/reader inputs above, which only describe how to build a resolver when none exists.
        ClaudeCredentialResolver? claudeCredentialResolver = null)
    {
        startInfo.Environment.Remove("CODEX_HOME");
        startInfo.Environment.Remove("CLAUDE_CONFIG_DIR");
        startInfo.Environment.Remove("GROK_HOME");
        startInfo.Environment.Remove("HERMES_HOME");
        startInfo.Environment.Remove(HarnessHookRootContract.EnvironmentVariableName);

        var hookRoot = HarnessHookRootContract.Apply(startInfo);
        var hookDiagnostic = HarnessHookRootContract.DescribeUnsupported(hookRoot);
        if (hookDiagnostic is not null && !string.IsNullOrWhiteSpace(stderrPath))
        {
            AppendDispatchStderrDiagnostic(stderrPath, hookDiagnostic);
        }

        if (provider == WorkerSandboxProvider.Codex)
        {
            var codexHome = Path.Combine(sandboxRoot, "codex-home");
            Directory.CreateDirectory(codexHome);
            SeedCodexAuth(codexHome);
            startInfo.Environment["CODEX_HOME"] = codexHome;
            return null;
        }

        if (provider == WorkerSandboxProvider.Claude)
        {
            return SeedClaudeEnvironment(
                startInfo,
                sandboxRoot,
                stderrPath,
                anthropicApiKeyAccessor,
                // One resolver for this dispatch: either the caller's resolved result travels in, or
                // this is the first consumer and the resolver it builds performs the single selection.
                claudeCredentialResolver ?? new ClaudeCredentialResolver(
                    environmentReader,
                    defaultHomeProvider,
                    claudeCredentialDirectoryAccessor));
        }

        if (provider == WorkerSandboxProvider.Grok)
        {
            SeedGrokEnvironment(startInfo, sandboxRoot, stderrPath);
            return null;
        }

        if (provider == WorkerSandboxProvider.Hermes)
        {
            SeedHermesEnvironment(startInfo, sandboxRoot);
        }

        return null;
    }

    internal static void SeedWorkerCaBundle(ProcessStartInfo startInfo, string sandboxRoot, string? stderrPath = null)
    {
        var bundlePath = Path.Combine(sandboxRoot, WorkerCaBundleFileName);
        try
        {
            Directory.CreateDirectory(sandboxRoot);
            if (!HasNonEmptyFile(bundlePath))
            {
                var existingBundle = startInfo.Environment.TryGetValue("SSL_CERT_FILE", out var existingPath)
                    ? existingPath
                    : Environment.GetEnvironmentVariable("SSL_CERT_FILE");
                if (!string.IsNullOrWhiteSpace(existingBundle) && File.Exists(existingBundle))
                {
                    File.Copy(existingBundle, bundlePath, overwrite: true);
                }
                else
                {
                    File.WriteAllText(bundlePath, ExportWindowsRootCertificateBundle());
                }
            }

            startInfo.Environment["SSL_CERT_FILE"] = bundlePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            if (!string.IsNullOrWhiteSpace(stderrPath))
            {
                AppendDispatchStderrDiagnostic(
                    stderrPath,
                    $"Worker sandbox diagnostic: failed to provision SSL_CERT_FILE bundle: {ex.Message}");
            }
        }
    }

    private static bool HasNonEmptyFile(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true, Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildPemCertificateBundle(IEnumerable<X509Certificate2> certificates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var certificate in certificates)
        {
            var hash = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            if (!seen.Add(hash))
            {
                continue;
            }

            builder.AppendLine(certificate.ExportCertificatePem().TrimEnd());
        }

        if (builder.Length == 0)
        {
            throw new InvalidOperationException("No exportable root CA certificates were available.");
        }

        return builder.ToString();
    }

    private static string ExportWindowsRootCertificateBundle()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Worker CA bundle export is only supported by this sandbox path on Windows.");
        }

        var certificates = new List<X509Certificate2>();
        AddCertificates(certificates, StoreLocation.LocalMachine, StoreName.Root);
        AddCertificates(certificates, StoreLocation.LocalMachine, StoreName.CertificateAuthority);
        AddCertificates(certificates, StoreLocation.CurrentUser, StoreName.Root);
        AddCertificates(certificates, StoreLocation.CurrentUser, StoreName.CertificateAuthority);
        return BuildPemCertificateBundle(certificates);
    }

    private static void AddCertificates(List<X509Certificate2> certificates, StoreLocation location, StoreName name)
    {
        try
        {
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            certificates.AddRange(store.Certificates);
        }
        catch (CryptographicException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Seeds the sandbox Claude config root from <paramref name="resolver"/>'s single resolved source
    /// and returns that resolved result, or null in API-key mode where no source is consumed.
    /// </summary>
    private static ClaudeCredentialResolution? SeedClaudeEnvironment(
        ProcessStartInfo startInfo,
        string sandboxRoot,
        string? stderrPath,
        Func<string?>? anthropicApiKeyAccessor,
        ClaudeCredentialResolver resolver)
    {
        var claudeConfigDir = Path.Combine(sandboxRoot, "claude-config");

        // Read through the resolver's own environment view so the API-key decision and the credential
        // selection cannot be answered by two different environments.
        var apiKey = anthropicApiKeyAccessor is not null
            ? anthropicApiKeyAccessor()
            : resolver.ReadEnvironment(ClaudeCredentialSource.ApiKeyEnvironmentVariable);

        ClaudeCredentialResolution? seededSource = null;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Explicit API-key precedence: subscription source validation is bypassed entirely, so
            // an unusable CLI login must neither fail nor warn in this mode.
            startInfo.Environment["ANTHROPIC_API_KEY"] = apiKey;
        }
        else
        {
            // Subscription auth: the resolver owns selection and validation, and seeding consumes
            // that one resolved result. It throws WorkerSubscriptionPreflightException (with the
            // sanitized diagnostic published to stderr first) before any destination artifact is
            // created, so dispatch stops rather than launching against a stale destination login.
            seededSource = ClaudeCredentialSource.SeedSubscriptionCredentials(
                claudeConfigDir,
                resolver,
                string.IsNullOrWhiteSpace(stderrPath)
                    ? null
                    : diagnostic => AppendDispatchStderrDiagnostic(stderrPath!, diagnostic));
        }

        ClaudeCredentialSource.EnsureSandboxSettings(claudeConfigDir);
        startInfo.Environment["CLAUDE_CONFIG_DIR"] = claudeConfigDir;
        return seededSource;
    }

    private static void AppendDispatchStderrDiagnostic(string stderrPath, string message)
    {
        var directory = Path.GetDirectoryName(stderrPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(stderrPath, message + Environment.NewLine);
    }

    internal static Stream OpenWorkerStderrStream(string stderrPath)
    {
        return new RollingLogWriteStream(stderrPath, FileMode.Append);
    }

    internal static string BuildLowIntegrityPath(string? currentPath, string shellExecutable, string? sandboxBin = null)
    {
        var entries = new List<string>();
        if (!string.IsNullOrWhiteSpace(sandboxBin))
        {
            entries.Add(sandboxBin);
        }

        var shellDirectory = Path.GetDirectoryName(shellExecutable);
        if (!string.IsNullOrWhiteSpace(shellDirectory))
        {
            entries.Add(shellDirectory);
        }

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            foreach (var rawEntry in currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsWindowsAppsPathSegment(rawEntry) ||
                    entries.Any(existing => PathsEqual(existing, rawEntry)))
                {
                    continue;
                }

                entries.Add(rawEntry);
            }
        }

        return string.Join(Path.PathSeparator, entries);
    }

    internal static string CreateSandboxBinDirectory(string sandboxRoot)
    {
        var sandboxBin = Path.Combine(sandboxRoot, "bin");
        Directory.CreateDirectory(sandboxBin);
        return sandboxBin;
    }

    internal static void WriteWorkerCommandShims(string sandboxBin, string? currentPath)
    {
        Directory.CreateDirectory(sandboxBin);
        WriteCommandShim(Path.Combine(sandboxBin, "git.cmd"), "git", ResolveExecutableWithWhere("git", currentPath));
        WriteCommandShim(Path.Combine(sandboxBin, "dotnet.cmd"), "dotnet", ResolveExecutableWithWhere("dotnet", currentPath));
    }

    internal static string ResolveExecutableWithWhere(string commandName, string? currentPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "where.exe" : "which",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(commandName);
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            startInfo.Environment["PATH"] = currentPath;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to resolve '{commandName}' with {startInfo.FileName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Timed out resolving '{commandName}' with {startInfo.FileName}.");
        }

        var resolved = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(File.Exists);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException($"Failed to resolve '{commandName}' with {startInfo.FileName}: {error.Trim()}");
        }

        return Path.GetFullPath(resolved);
    }

    internal static void WriteCommandShim(string shimPath, string commandName, string realExecutable)
    {
        var escapedExecutable = realExecutable.Replace("%", "%%", StringComparison.Ordinal);
        var contents = $"""
            @echo off
            setlocal EnableExtensions EnableDelayedExpansion
            set "MCG_REAL={escapedExecutable}"
            set "MCG_ATTEMPT=1"
            set "MCG_STDERR=%TEMP%\mcg-{commandName}-shim-%RANDOM%-%RANDOM%.err"
            :retry
            "%MCG_REAL%" %* 2>"%MCG_STDERR%"
            set "MCG_EXIT=!ERRORLEVEL!"
            type "%MCG_STDERR%" 1>&2 2>nul
            if "!MCG_EXIT!"=="0" (
                del "%MCG_STDERR%" >nul 2>nul
                exit /b 0
            )
            findstr /i /c:"CreateProcessAsUserW 1312" /c:"specified logon session does not exist" "%MCG_STDERR%" >nul 2>nul
            if errorlevel 1 (
                del "%MCG_STDERR%" >nul 2>nul
                exit /b !MCG_EXIT!
            )
            if "!MCG_ATTEMPT!"=="3" (
                echo [mcg-shim] CreateProcessAsUserW 1312 retry exhausted for {commandName}. 1>&2
                del "%MCG_STDERR%" >nul 2>nul
                exit /b !MCG_EXIT!
            )
            set /a MCG_ATTEMPT+=1
            if "!MCG_ATTEMPT!"=="2" (
                "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "Start-Sleep -Milliseconds 250" >nul 2>nul
            ) else (
                "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "Start-Sleep -Milliseconds 750" >nul 2>nul
            )
            goto retry
            """;
        File.WriteAllText(shimPath, contents.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    internal static void RunLowIntegrityLaunchPreflight(ProcessStartInfo workerStartInfo, DispatchRunParameters parameters)
    {
        if (!parameters.SandboxLowIntegrity || !OperatingSystem.IsWindows())
        {
            return;
        }

        RunLowIntegrityLaunchPreflightCommand("git", parameters.WorkingDirectory, workerStartInfo.Environment["PATH"]);
        RunLowIntegrityLaunchPreflightCommand("dotnet", parameters.WorkingDirectory, workerStartInfo.Environment["PATH"]);
    }

    private static void RunLowIntegrityLaunchPreflightCommand(string commandName, string workingDirectory, string? path)
    {
        var commandPath = ResolveCommandFromPath(commandName, path)
            ?? throw new InvalidOperationException($"Low-integrity launch preflight could not resolve '{commandName}' from the shimmed PATH.");
        var startInfo = new ProcessStartInfo
        {
            FileName = commandPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows() &&
            (commandPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             commandPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"\"{commandPath}\" --version\"";
        }
        else
        {
            startInfo.ArgumentList.Add("--version");
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            startInfo.Environment["PATH"] = path;
        }
        startInfo.Environment.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Low-integrity launch preflight failed to start '{commandName} --version'.");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Low-integrity launch preflight timed out running '{commandName} --version'.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Low-integrity launch preflight failed for '{commandName} --version' with exit {process.ExitCode}: {stderr.Trim()} {stdout.Trim()}".Trim());
        }
    }

    internal static string? ResolveCommandFromPath(string commandName, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".exe", ".bat", ".com" }
            : new[] { string.Empty };
        foreach (var rawEntry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(rawEntry, commandName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static bool IsWindowsAppsPathSegment(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        return normalized.EndsWith(
            $"{Path.DirectorySeparatorChar}Microsoft{Path.DirectorySeparatorChar}WindowsApps",
            StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(
                $"{Path.DirectorySeparatorChar}WindowsApps",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            left = Path.GetFullPath(left);
            right = Path.GetFullPath(right);
        }
        catch
        {
            // Compare the original strings when either path is malformed.
        }

        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // Appends ".mcg-sandbox/" to the worktree's local git exclude (.git/info/exclude, resolved via
    // rev-parse so linked worktrees resolve correctly). Local-only and untracked, so it confines the
    // sandbox scratch without dirtying the goal branch. Idempotent.
    private static void ExcludeSandboxFromGit(string worktree)
    {
        try
        {
            var pathResult = GitCli.Run(worktree, "rev-parse", "--git-path", "info/exclude");
            if (!pathResult.Succeeded || string.IsNullOrWhiteSpace(pathResult.Output))
            {
                return;
            }

            var excludeRaw = pathResult.Output.Trim();
            var excludePath = Path.IsPathRooted(excludeRaw)
                ? excludeRaw
                : Path.GetFullPath(Path.Combine(worktree, excludeRaw));
            Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);

            var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : string.Empty;
            if (existing.Contains(".mcg-sandbox", StringComparison.Ordinal))
            {
                return;
            }

            var prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
            File.AppendAllText(excludePath, prefix + ".mcg-sandbox/\n");
        }
        catch
        {
            // Best-effort: if git ignores fail to write, the worktree inspection will simply see the
            // scratch dir; the commit recovery still excludes it via pathspec.
        }
    }

    internal static IWorkerIntegrityLabeler? IntegrityLabelerOverrideForTests;

    internal sealed class HeartbeatWriteGuard(string destinationPath, bool durable)
    {
        private readonly object _gate = new();
        private bool _terminalWritten;

        internal static Action<string, string>? BeforeMoveForTests;

        internal void Write(string state, string payload) =>
            WriteCore(state, payload, terminal: false);

        internal void WriteTerminal(string state, string payload) =>
            WriteCore(state, payload, terminal: true);

        private void WriteCore(string state, string payload, bool terminal)
        {
            string? temporaryPath = null;
            try
            {
                if (!terminal && Volatile.Read(ref _terminalWritten))
                {
                    return;
                }

                temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
                if (durable)
                {
                    WriteAllTextDurable(temporaryPath, payload);
                }
                else
                {
                    File.WriteAllText(temporaryPath, payload);
                }

                BeforeMoveForTests?.Invoke(destinationPath, state);
                lock (_gate)
                {
                    if (!terminal && _terminalWritten)
                    {
                        return;
                    }

                    File.Move(temporaryPath, destinationPath, overwrite: true);
                    temporaryPath = null;
                    if (terminal)
                    {
                        _terminalWritten = true;
                    }
                }
            }
            catch
            {
                // Heartbeats are best-effort; publication failures must not fail the dispatch.
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); } catch { }
                }
            }
        }
    }

    private static IWorkerIntegrityLabeler ResolveIntegrityLabeler() =>
        IntegrityLabelerOverrideForTests ?? IntegrityLabeler;

    internal static void ProtectWorkspaceBoundary(string worktree) =>
        ProtectWorkspaceBoundary(worktree, ResolveIntegrityLabeler());

    internal static void ProtectWorkspaceBoundary(
        string worktree,
        IWorkerIntegrityLabeler integrityLabeler)
    {
        var parent = Directory.GetParent(worktree);
        if (parent is null || !parent.Exists)
        {
            return;
        }

        // Label ONLY the parent directory node, non-inheritable. An inheritable (OI)(CI) label makes
        // Windows re-propagate labels through every existing descendant (~20k nodes/sec measured),
        // which blew the two-minute icacls cap on large worktree groves and silently left the
        // boundary UNLABELED. Create/delete under the parent is governed by the parent's own label,
        // so one node suffices; sibling worktree interiors are covered by their own labels.
        var state = integrityLabeler.Query(parent.FullName);
        var confirmedMedium = state.Exists && state.Medium && !state.Low;
        if (!confirmedMedium && !integrityLabeler.SetIntegrity(parent.FullName, "M", recursive: false))
        {
            throw new InvalidOperationException($"Failed to protect workspace boundary '{parent.FullName}'.");
        }

        foreach (var file in parent.EnumerateFiles())
        {
            _ = SetMediumIntegrity(file.FullName, integrityLabeler);
        }
    }

    // PowerShell that lowers the current process to Low integrity (lowering one's own token needs no
    // privilege). Dot-sourced before the worker command so the worker + its children run Low.
    internal const string DropToLowScript = @"Add-Type -Namespace P -Name N -MemberDefinition @'
[DllImport(""kernel32.dll"")] public static extern System.IntPtr GetCurrentProcess();
[DllImport(""advapi32.dll"", SetLastError=true)] public static extern bool OpenProcessToken(System.IntPtr h, uint a, out System.IntPtr t);
[DllImport(""advapi32.dll"", SetLastError=true, CharSet=CharSet.Unicode)] public static extern bool ConvertStringSidToSidW(string s, out System.IntPtr sid);
[DllImport(""advapi32.dll"", SetLastError=true)] public static extern bool SetTokenInformation(System.IntPtr t, int c, ref TML info, int len);
[StructLayout(LayoutKind.Sequential)] public struct SAA { public System.IntPtr Sid; public uint Attr; }
[StructLayout(LayoutKind.Sequential)] public struct TML { public SAA Label; }
public static void DropToLow() {
    System.IntPtr tok, sid;
    if (!OpenProcessToken(GetCurrentProcess(), 0x0088, out tok)) throw new System.ComponentModel.Win32Exception();
    if (!ConvertStringSidToSidW(""S-1-16-4096"", out sid)) throw new System.ComponentModel.Win32Exception();
    var t = new TML(); t.Label.Sid = sid; t.Label.Attr = 0x20;
    if (!SetTokenInformation(tok, 25, ref t, Marshal.SizeOf(typeof(TML))+16)) throw new System.ComponentModel.Win32Exception();
}
'@
[P.N]::DropToLow()
";

    internal static void ProtectGitMetadata(string worktree) =>
        ProtectGitMetadata(worktree, ResolveIntegrityLabeler());

    internal static void ProtectGitMetadata(
        string worktree,
        IWorkerIntegrityLabeler integrityLabeler)
    {
        var checkoutGitFile = Path.Combine(worktree, ".git");
        if (!File.Exists(checkoutGitFile) && !Directory.Exists(checkoutGitFile))
        {
            return;
        }

        if (File.Exists(checkoutGitFile))
        {
            if (!SetMediumIntegrity(checkoutGitFile, integrityLabeler))
            {
                throw new InvalidOperationException($"Failed to protect linked worktree git file '{checkoutGitFile}'.");
            }
        }

        string? commonDir = null;
        try
        {
            var commonDirResult = GitCli.Run(worktree, "rev-parse", "--git-common-dir");
            if (!commonDirResult.Succeeded || string.IsNullOrWhiteSpace(commonDirResult.Output))
            {
                return;
            }

            var commonDirRaw = commonDirResult.Output.Trim();
            commonDir = Path.IsPathRooted(commonDirRaw)
                ? commonDirRaw
                : Path.GetFullPath(Path.Combine(worktree, commonDirRaw));
        }
        catch
        {
            // Git metadata protection is best-effort; the checkout-local .git file is handled directly
            // above so linked worktrees keep their write confinement even when git probing fails.
        }

        if (commonDir is not null && Directory.Exists(commonDir) && !SetMediumIntegrity(commonDir, integrityLabeler))
        {
            throw new InvalidOperationException($"Failed to protect git common dir '{commonDir}'.");
        }
    }

    private static bool SetMediumIntegrity(string path, IWorkerIntegrityLabeler? integrityLabeler = null)
    {
        return (integrityLabeler ?? ResolveIntegrityLabeler()).SetIntegrity(
            path,
            Directory.Exists(path) ? "(OI)(CI)M" : "M",
            recursive: false);
    }

    internal static bool WaitForIntegrityLabeler(Process process, TimeSpan timeout)
    {
        if (process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            return true;
        }

        try { process.Kill(entireProcessTree: true); } catch { }
        try { process.WaitForExit(5000); } catch { }
        return false;
    }

    private static void WriteLowIntegritySetupArtifact(
        string sandboxRoot,
        string worktree,
        WorkerSandboxPreparationResult preparation,
        ClaudeCredentialResolution? seededCredentialSource = null)
    {
        var artifact = new
        {
            strategy = "prepared-root-inherited-low-integrity",
            worktreeRecursiveRelabel = preparation.WorktreeRecursiveRelabel,
            sandboxRecursiveRelabel = preparation.SandboxRecursiveRelabel,
            prepReceiptHit = preparation.PrepReceiptHit,
            sandboxRoot,
            worktree,
            // Source kind, directory and status only - never token, refresh-token or API-key material
            // and never credential file contents. Null in API-key mode and for other providers.
            credentialSource = seededCredentialSource is null ? null : new
            {
                directory = seededCredentialSource.Inspection.DirectoryPath,
                isExplicitSource = seededCredentialSource.Inspection.IsExplicitSource,
                status = seededCredentialSource.Inspection.Status.ToString()
            }
        };
        File.WriteAllText(
            Path.Combine(sandboxRoot, LowIntegritySetupArtifactName),
            JsonSerializer.Serialize(artifact, JsonOptions) + Environment.NewLine);
    }

    private static void SeedCodexAuth(string codexHome)
    {
        try
        {
            var src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(codexHome, "auth.json"), overwrite: true);
            }
        }
        catch { /* best-effort */ }
    }

    internal static void SeedGrokEnvironment(
        ProcessStartInfo startInfo,
        string sandboxRoot,
        string? stderrPath = null,
        Func<string>? grokHomeDirectoryAccessor = null)
    {
        var grokHome = Path.Combine(sandboxRoot, "grok-home");
        Directory.CreateDirectory(grokHome);

        var userGrokDir = (grokHomeDirectoryAccessor ?? ResolveGrokHomeDirectory)();
        var seededAuth = false;
        foreach (var fileName in new[] { "auth.json", "config.toml", "trusted_folders.toml" })
        {
            try
            {
                var source = Path.Combine(userGrokDir, fileName);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(grokHome, fileName), overwrite: true);
                    seededAuth = seededAuth || fileName == "auth.json";
                }
            }
            catch
            {
                // Best-effort seed; a missing auth file is reported below.
            }
        }

        if (!seededAuth && !string.IsNullOrWhiteSpace(stderrPath))
        {
            AppendDispatchStderrDiagnostic(
                stderrPath,
                "Grok worker sandbox diagnostic: no auth.json was found to seed into GROK_HOME; Grok may fail to authenticate.");
        }

        startInfo.Environment["GROK_HOME"] = grokHome;
        startInfo.Environment["GROK_DISABLE_AUTOUPDATER"] = "1";
    }

    internal static void SeedHermesEnvironment(ProcessStartInfo startInfo, string sandboxRoot)
    {
        var hermesHome = Path.Combine(sandboxRoot, $"hermes-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hermesHome);
        startInfo.Environment["HERMES_HOME"] = hermesHome;
        startInfo.Environment["HERMES_ACP_SKIP_CONFIGURED_MCP"] = "1";
    }

    private static string ResolveGrokHomeDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");

    /// <summary>Entry point for the detached <c>__dispatch-run &lt;paramsPath&gt;</c> subcommand.</summary>
    public static int Run(string parametersPath)
    {
        DispatchRunParameters parameters;
        try
        {
            parameters = ReadParameters(parametersPath);
        }
        catch
        {
            return 1;
        }

        return RunCore(parameters);
    }

    private static int RunCore(DispatchRunParameters parameters)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var lastProgressAt = startedAt;
        long lastStdoutBytes = -1;
        long lastStderrBytes = -1;
        long lastCpuMs = 0L;
        var exitCode = 1;
        var providerSessionId = NormalizeProviderSessionId(parameters.ProviderSessionId);
        var stdoutSessionCapture = new ProviderSessionCaptureState();
        var stderrSessionCapture = new ProviderSessionCaptureState();
        var prepStarted = false;
        var prepExitWritten = false;
        Process? worker = null;
        OwnedProcessGroup? workerGroup = null;
        Process? selectedChild = null;
        var selectedChildLock = new object();
        var heartbeatProcessIdentities = new DispatchHeartbeatProcessIdentityTracker();
        string? hostDiagnosticWriteFailure = null;
        var heartbeatInterval = parameters.HeartbeatIntervalMilliseconds > 0
            ? TimeSpan.FromMilliseconds(parameters.HeartbeatIntervalMilliseconds)
            : HeartbeatInterval;
        var heartbeatWriter = string.IsNullOrWhiteSpace(parameters.HeartbeatPath)
            ? null
            : new HeartbeatWriteGuard(parameters.HeartbeatPath, durable: false);
        var prepHeartbeatWriter = string.IsNullOrWhiteSpace(parameters.PrepHeartbeatPath)
            ? null
            : new HeartbeatWriteGuard(parameters.PrepHeartbeatPath, durable: true);

        void RecordFallbackDiagnostic(string diagnostic)
        {
            hostDiagnosticWriteFailure = diagnostic;
            if (string.IsNullOrWhiteSpace(parameters.HostDiagnosticPath))
            {
                return;
            }

            try
            {
                AppendAllTextDurable(parameters.HostDiagnosticPath, diagnostic + Environment.NewLine);
            }
            catch (Exception fallbackFailure)
            {
                hostDiagnosticWriteFailure +=
                    $"; fallback diagnostic write failed: {fallbackFailure.GetType().Name}: {fallbackFailure.Message}";
            }
        }

        void ObserveSelectedChild(int? candidatePid)
        {
            if (candidatePid is null || worker is null || candidatePid.Value == worker.Id)
            {
                return;
            }

            lock (selectedChildLock)
            {
                if (selectedChild?.Id == candidatePid.Value)
                {
                    return;
                }

                try
                {
                    // Opening the Process while it is alive retains an OS handle, so ExitCode remains
                    // available after the selected CLI child disappears from process enumeration.
                    var observedChild = Process.GetProcessById(candidatePid.Value);
                    try
                    {
                        _ = observedChild.SafeHandle;
                        selectedChild?.Dispose();
                        selectedChild = observedChild;
                    }
                    catch
                    {
                        observedChild.Dispose();
                        throw;
                    }
                }
                catch
                {
                    // The next watchdog/heartbeat observation can retry if the candidate raced exit.
                }
            }
        }

        void WriteSelectedChildExitRecord()
        {
            if (string.IsNullOrWhiteSpace(parameters.ChildExitRecordPath))
            {
                return;
            }

            lock (selectedChildLock)
            {
                try
                {
                    var childProcessId = selectedChild?.Id ?? 0;
                    int? childExitCode = null;
                    var state = "not-observed";
                    if (selectedChild is not null)
                    {
                        if (!selectedChild.HasExited)
                        {
                            selectedChild.WaitForExit(1000);
                        }

                        if (selectedChild.HasExited)
                        {
                            childExitCode = selectedChild.ExitCode;
                            state = "exited";
                        }
                        else
                        {
                            state = "running-after-root-exit";
                        }
                    }

                    WriteAllTextDurable(
                        parameters.ChildExitRecordPath,
                        JsonSerializer.Serialize(
                            new DispatchChildExitRecord(
                                childProcessId,
                                childExitCode,
                                DateTimeOffset.UtcNow,
                                state),
                            JsonOptions));
                }
                catch (Exception ex)
                {
                    RecordFallbackDiagnostic(
                        $"[dispatch-host] selected child exit record failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        void RecordHostFailure(Exception failure)
        {
            var diagnostic = $"[dispatch-host] worker launch/run failed: {failure}\n";
            try
            {
                File.AppendAllText(parameters.StderrPath, diagnostic);
            }
            catch (Exception appendFailure)
            {
                RecordFallbackDiagnostic(
                    $"[dispatch-host] stderr diagnostic append failed: {appendFailure.GetType().Name}: {appendFailure.Message}; " +
                    $"original failure: {failure.GetType().Name}: {failure.Message}");
            }
        }

        void WriteHeartbeat(string state, bool terminal = false)
        {
            if (heartbeatWriter is null)
            {
                return;
            }

            var stdoutBytes = FileLength(parameters.StdoutPath);
            var stderrBytes = FileLength(parameters.StderrPath);
            var (ownedPids, ownedProcessIdentities) = CaptureHeartbeatOwnership();
            var ownedAccounting = ReadHeartbeatOwnedAccounting(workerGroup);
            var ownedCpuMs = ownedAccounting?.CpuMilliseconds ?? SumOwnedCpuMs(ownedPids);
            var ownedPeakMemoryBytes = ownedAccounting?.PeakMemoryBytes;
            var ownedIoBytes = ownedAccounting?.IoBytes;
            var childPid = SelectHeartbeatChildPid(worker, ownedPids);
            ObserveSelectedChild(childPid);
            providerSessionId ??= TryCaptureProviderSessionId(
                parameters.Provider,
                parameters.StdoutPath,
                parameters.StderrPath,
                stdoutSessionCapture,
                stderrSessionCapture);

            if (HasProgressed(lastStdoutBytes + lastStderrBytes, stdoutBytes + stderrBytes, lastCpuMs, ownedCpuMs, CpuProgressEpsilonMs))
            {
                lastProgressAt = DateTimeOffset.UtcNow;
            }

            if (stdoutBytes != lastStdoutBytes || stderrBytes != lastStderrBytes)
            {
                lastStdoutBytes = stdoutBytes;
                lastStderrBytes = stderrBytes;
            }

            lastCpuMs = ownedCpuMs;

            var payload = new
            {
                kind = string.IsNullOrWhiteSpace(parameters.Kind) ? WorkerDispatchKind : parameters.Kind,
                pid = Environment.ProcessId,
                childPid,
                ownedPids,
                ownedProcessIdentities,
                startedAt = startedAt.ToString("o"),
                lastObservedAt = DateTimeOffset.UtcNow.ToString("o"),
                lastProgressAt = lastProgressAt.ToString("o"),
                state,
                stdoutBytes,
                stderrBytes,
                ownedCpuMs,
                ownedPeakMemoryBytes,
                ownedIoBytes,
                providerSessionId,
                sessionCaptureStdoutOffset = stdoutSessionCapture.Offset,
                sessionCaptureStderrOffset = stderrSessionCapture.Offset,
                sessionCaptureGaveUp = stderrSessionCapture.GaveUp,
                worktreeHeadSha = parameters.WorktreeHeadSha,
                dirtyStateHash = parameters.DirtyStateHash,
                hostDiagnosticWriteFailure,
                exitFileExists = File.Exists(parameters.ExitCodePath)
            };

            var serializedPayload = JsonSerializer.Serialize(payload, JsonOptions);
            if (terminal)
            {
                heartbeatWriter.WriteTerminal(state, serializedPayload);
            }
            else
            {
                heartbeatWriter.Write(state, serializedPayload);
            }
        }

        (IReadOnlyList<int> OwnedPids, IReadOnlyList<SpawnProcessIdentity> RecordedIdentities) CaptureHeartbeatOwnership()
        {
            var candidateOwnedPids = GetHeartbeatOwnedProcessIds(workerGroup, worker);
            var candidateSet = candidateOwnedPids.ToHashSet();
            var identitySnapshot = heartbeatProcessIdentities.Capture(
                Environment.ProcessId,
                candidateOwnedPids,
                () => workerGroup?.TryGetActiveProcessIds(out var currentOwnedPids) == true
                    ? currentOwnedPids
                    : candidateOwnedPids);
            var identityBoundOwnedPids = identitySnapshot.Current
                .Where(identity => identity.ProcessId != Environment.ProcessId && candidateSet.Contains(identity.ProcessId))
                .Select(identity => identity.ProcessId)
                .Distinct()
                .OrderBy(processId => processId)
                .ToArray();
            return (identityBoundOwnedPids, identitySnapshot.Recorded);
        }

        void WritePrepHeartbeat(string state, bool terminal = false)
        {
            if (prepHeartbeatWriter is null)
            {
                return;
            }

            var payload = new
            {
                kind = PrepDispatchKind,
                pid = Environment.ProcessId,
                ownedPids = GetHeartbeatOwnedProcessIds(workerGroup, worker),
                startedAt = startedAt.ToString("o"),
                lastObservedAt = DateTimeOffset.UtcNow.ToString("o"),
                lastProgressAt = DateTimeOffset.UtcNow.ToString("o"),
                state,
                exitFileExists = !string.IsNullOrWhiteSpace(parameters.PrepExitCodePath) && File.Exists(parameters.PrepExitCodePath)
            };

            var serializedPayload = JsonSerializer.Serialize(payload, JsonOptions);
            if (terminal)
            {
                prepHeartbeatWriter.WriteTerminal(state, serializedPayload);
            }
            else
            {
                prepHeartbeatWriter.Write(state, serializedPayload);
            }
        }

        void CompletePrep(int prepExitCode)
        {
            if (!prepStarted || prepExitWritten || string.IsNullOrWhiteSpace(parameters.PrepExitCodePath))
            {
                return;
            }

            TryWriteExitCode(parameters.PrepExitCodePath, prepExitCode);
            prepExitWritten = true;
            if (!string.IsNullOrWhiteSpace(parameters.PrepRecordPath))
            {
                try
                {
                    WritePrepRecord(parameters.PrepRecordPath, new DispatchPrepRecord(
                        PrepDispatchKind,
                        parameters.PrepGoalId ?? string.Empty,
                        parameters.PrepTaskId ?? string.Empty,
                        parameters.WorkingDirectory,
                        parameters.PrepHeartbeatPath ?? string.Empty,
                        parameters.PrepExitCodePath,
                        startedAt,
                        parameters.Provider,
                        parameters.SandboxWorktreeWritable,
                        DateTimeOffset.UtcNow));
                }
                catch
                {
                    // The already-persisted prep record remains recoverable through heartbeat/exit artifacts.
                }
            }

            WritePrepHeartbeat(prepExitCode == 0 ? "exited" : "failed", terminal: true);
        }

        using var heartbeatTimer = new Timer(_ => WriteHeartbeat("running"), null, Timeout.Infinite, Timeout.Infinite);
        using var prepHeartbeatTimer = new Timer(_ => WritePrepHeartbeat("preparing-sandbox"), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            // Redirect stdin so we can close it immediately: CLI workers otherwise inherit the
            // orchestrator's stdin. Under a background/detached launch that handle is an open pipe
            // that never reaches EOF, so the worker blocks indefinitely waiting for stdin.
            var startInfo = WorkerProcessRunner.BuildPowerShellStartInfo(parameters.Command, parameters.WorkingDirectory);
            PrependMandatoryContextAuthorityPreflight(startInfo, parameters);

            if (parameters.DisableSharedCompilation)
            {
                startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                startInfo.Environment["UseSharedCompilation"] = "false";
            }

            WriteHeartbeat(parameters.SandboxLowIntegrity ? "preparing-sandbox" : "starting");
            if (parameters.SandboxLowIntegrity && !string.IsNullOrWhiteSpace(parameters.PrepHeartbeatPath))
            {
                prepStarted = true;
                WritePrepHeartbeat("preparing-sandbox");
                prepHeartbeatTimer.Change(heartbeatInterval, heartbeatInterval);
            }

            var sandboxPrepStartedAt = DateTimeOffset.UtcNow;
            WriteSandboxPrepEvent(parameters, "start", sandboxPrepStartedAt, null);
            var sandboxPreparation = ApplyWorkerSandbox(
                startInfo,
                parameters,
                WorkerSandboxPreparer.CreateDefault(),
                (phase, stepStartedAt, elapsed) => WriteSandboxPrepEvent(parameters, phase, stepStartedAt, elapsed));
            WriteSandboxPrepEvent(
                parameters,
                sandboxPreparation.PrepReceiptHit ? "receipt-hit" : "complete",
                sandboxPrepStartedAt,
                DateTimeOffset.UtcNow - sandboxPrepStartedAt);

            WriteHeartbeat(parameters.SandboxLowIntegrity ? "preflighting-sandbox" : "starting");
            WritePrepHeartbeat(parameters.SandboxLowIntegrity ? "preflighting-sandbox" : "starting");
            var preflightStartedAt = DateTimeOffset.UtcNow;
            RunLowIntegrityLaunchPreflight(startInfo, parameters);
            WriteSandboxPrepEvent(parameters, "launch-preflight", preflightStartedAt, DateTimeOffset.UtcNow - preflightStartedAt);
            prepHeartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            CompletePrep(0);

            WriteHeartbeat("starting");
            using var mandatoryContextLeases = AcquireMandatoryContextFileLeases(parameters);
            RequireStartGate();
            startInfo.Environment[WorkerSandboxOptions.DispatchWorkerVariable] = "1";
            worker = ProcessTreeGuiSuppression.Start(startInfo);
            workerGroup = OwnedProcessGroup.Attach(worker);

            WritePromptToWorkerStdin(worker, parameters);

            // Stream raw bytes to the log files so the heartbeat's byte-growth progress detection works.
            using var stdout = new RollingLogWriteStream(parameters.StdoutPath, FileMode.Create);
            using var stderr = OpenWorkerStderrStream(parameters.StderrPath);
            using var drainCts = new CancellationTokenSource();
            var copyOut = worker.StandardOutput.BaseStream.CopyToAsync(stdout, drainCts.Token);
            var copyErr = worker.StandardError.BaseStream.CopyToAsync(stderr, drainCts.Token);

            heartbeatTimer.Change(heartbeatInterval, heartbeatInterval);

            // Bounded supervision instead of an unbounded WaitForExit: reap the whole tree when
            // ShouldReapWorker says so (runtime cap, or idle/stall cap once the worker has streamed).
            var maxRuntime = ResolveWatchdogTimeout("MCG_DISPATCH_MAX_RUNTIME_MIN", DefaultMaxRuntime);
            var maxIdle = ResolveWatchdogTimeout("MCG_DISPATCH_MAX_IDLE_MIN", DefaultMaxIdle);
            while (!worker.WaitForExit((int)WatchdogProbeInterval.TotalMilliseconds))
            {
                var (ownedPids, _) = CaptureHeartbeatOwnership();
                ObserveSelectedChild(SelectHeartbeatChildPid(worker, ownedPids));
                var now = DateTimeOffset.UtcNow;
                var runFor = now - startedAt;
                var idleFor = now - lastProgressAt;
                var hasProducedOutput = lastStdoutBytes > 0 || lastStderrBytes > 0;
                if (!ShouldReapWorker(runFor, idleFor, hasProducedOutput, maxRuntime, maxIdle))
                {
                    continue;
                }

                var reason = runFor >= maxRuntime
                    ? $"exceeded max runtime {maxRuntime.TotalMinutes:0} min"
                    : $"stalled {idleFor.TotalMinutes:0} min with no output (idle cap {maxIdle.TotalMinutes:0} min)";
                try { File.AppendAllText(parameters.StderrPath, $"\n[dispatch-host] terminating worker tree: {reason}.\n"); }
                catch { /* diagnostics are best-effort */ }
                TryKillWorkerTree(worker, workerGroup);
                worker.WaitForExit(5000);
                break;
            }

            // Drain with a bounded timeout. A grandchild that inherits the pipe handle
            // (e.g. claude-cli's node child) keeps CopyToAsync alive indefinitely after
            // the worker exits. Cap the wait and cancel so the finally block always
            // writes the exit-code file.
            var drainPolicy = (parameters.OutputDrainPolicy ?? ProcessOutputDrainPolicy.Default).Validate();
            var drainTasks = new Task[] { copyOut, copyErr };
            if (!Task.WaitAll(drainTasks, drainPolicy.Completion))
            {
                drainCts.Cancel();
                TryKillWorkerTree(worker, workerGroup);
                try { Task.WaitAll(drainTasks, drainPolicy.CancellationGrace); } catch { }
            }

            try { stdout.Flush(); } catch { }
            try { stderr.Flush(); } catch { }
            exitCode = worker.ExitCode;
        }
        catch (Exception ex)
        {
            exitCode = 1;
            prepHeartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            CompletePrep(1);
            // Capture launch/setup failures (e.g. launch-as-user under the OS sandbox) — otherwise the
            // worker never starts and nothing explains why (no worker means no redirected stderr).
            RecordHostFailure(ex);
        }
        finally
        {
            heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            prepHeartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            CompletePrep(exitCode == 0 ? 0 : 1);
            // One final heartbeat synchronizes the selected-child handle with the childPid receipt.
            // Freeze that selection into the child record before publishing the completion signal.
            WriteHeartbeat("exited", terminal: true);
            WriteSelectedChildExitRecord();
            WorkerProcessJobs.ReadAccountingAndDispose(workerGroup, kill: false, captureAccounting: false, out _);
            // The exit file is the completion signal consumed by BackgroundDispatchRunner. Publish it
            // only after every child/diagnostic artifact the completion path reads is durable.
            TryWriteDispatchExitArtifact(parameters.ExitCodePath, exitCode);
            selectedChild?.Dispose();
        }

        return exitCode;
    }

    private sealed class MandatoryContextFileLeaseSet : IDisposable
    {
        public static MandatoryContextFileLeaseSet Empty { get; } = new([]);
        private readonly IReadOnlyList<FileStream> _streams;

        public MandatoryContextFileLeaseSet(IReadOnlyList<FileStream> streams)
        {
            _streams = streams;
        }

        public void Dispose()
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    internal static string? TryCaptureProviderSessionId(
        WorkerSandboxProvider provider,
        string stdoutPath,
        string stderrPath,
        ProviderSessionCaptureState stdoutState,
        ProviderSessionCaptureState stderrState)
    {
        if (provider != WorkerSandboxProvider.Codex)
        {
            return null;
        }

        return TryCaptureProviderSessionIdFromLog(stdoutPath, stdoutState) ??
            TryCaptureProviderSessionIdFromLog(stderrPath, stderrState);
    }

    internal static string? TryCaptureProviderSessionIdFromLog(
        string path,
        ProviderSessionCaptureState state)
    {
        lock (state)
        {
            if (state.GaveUp)
            {
                return null;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var currentLength = new FileInfo(path).Length;
                if (currentLength == state.Offset)
                {
                    return null;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < state.Offset)
                {
                    state.Reset();
                }

                var remaining = ProviderSessionCaptureByteLimit - state.InspectedBytes;
                if (remaining <= 0)
                {
                    state.GaveUp = true;
                    return null;
                }

                stream.Seek(state.Offset, SeekOrigin.Begin);
                var requested = (int)Math.Min(remaining, Math.Max(0, stream.Length - state.Offset));
                if (requested == 0)
                {
                    return null;
                }

                var buffer = new byte[requested];
                var read = stream.Read(buffer, 0, buffer.Length);
                state.Offset += read;
                state.InspectedBytes += read;
                var text = state.PendingText + Encoding.UTF8.GetString(buffer, 0, read);
                foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (TryCaptureProviderSessionIdFromLine(line, out var sessionId))
                    {
                        return sessionId;
                    }
                }

                var lastLineBreak = text.LastIndexOfAny(['\r', '\n']);
                state.PendingText = lastLineBreak >= 0 ? text[(lastLineBreak + 1)..] : text;
                if (state.PendingText.Length > 2048)
                {
                    state.PendingText = state.PendingText[^2048..];
                }

                if (state.InspectedBytes >= ProviderSessionCaptureByteLimit)
                {
                    state.GaveUp = true;
                }
            }
            catch
            {
                // Session capture is best-effort; a missing id simply forces future resume attempts fresh.
            }

            return null;
        }
    }

    internal sealed class ProviderSessionCaptureState
    {
        public long Offset { get; set; }
        public int InspectedBytes { get; set; }
        public bool GaveUp { get; set; }
        public string PendingText { get; set; } = string.Empty;

        public void Reset()
        {
            Offset = 0;
            InspectedBytes = 0;
            GaveUp = false;
            PendingText = string.Empty;
        }
    }

    internal static bool TryCaptureProviderSessionIdFromLine(string line, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var jsonSessionId = TryCaptureJsonProviderSessionId(line);
        if (!string.IsNullOrWhiteSpace(jsonSessionId))
        {
            sessionId = jsonSessionId;
            return true;
        }

        var match = Regex.Match(
            line,
            @"\b(?:session(?:\s+id)?|session_id|sessionId)\b\s*[:=]\s*[""']?(?<id>[A-Za-z0-9][A-Za-z0-9._:-]{7,})[""']?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        sessionId = match.Groups["id"].Value;
        return true;
    }

    private static string? TryCaptureJsonProviderSessionId(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return TryGetJsonString(root, "session_id") ??
                TryGetJsonString(root, "sessionId") ??
                (JsonMentionsSession(root) ? TryGetJsonString(root, "id") : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetJsonString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;
    }

    private static bool JsonMentionsSession(JsonElement root)
    {
        return (TryGetJsonString(root, "type")?.Contains("session", StringComparison.OrdinalIgnoreCase) == true) ||
            (TryGetJsonString(root, "event")?.Contains("session", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string? NormalizeProviderSessionId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void TryKillWorkerTree(Process worker, OwnedProcessGroup? workerGroup)
    {
        try
        {
            WorkerProcessJobs.ReadAccountingAndDispose(workerGroup, kill: true, captureAccounting: false, out _);
        }
        catch
        {
            // Best-effort: fall back to direct tree kill below.
        }

        try
        {
            worker.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: worker may have already exited.
        }
    }

    // The idle/stall cap measures byte-growth stalls, which only have meaning once a worker has
    // streamed output. A worker that has produced NO output yet (e.g. claude-cli -p buffers all
    // output to the end) is bounded by maxRuntime alone, so a healthy buffering worker is not
    // false-positive-reaped mid-task; a worker that streamed then went quiet is still reaped on idle.
    internal static bool ShouldReapWorker(
        TimeSpan runFor,
        TimeSpan idleFor,
        bool hasProducedOutput,
        TimeSpan maxRuntime,
        TimeSpan maxIdle)
    {
        if (runFor >= maxRuntime)
        {
            return true;
        }

        return hasProducedOutput && idleFor >= maxIdle;
    }

    internal static bool HasProgressed(long prevTotalBytes, long curTotalBytes, long prevCpuMs, long curCpuMs, long epsilonMs)
        => curTotalBytes != prevTotalBytes || curCpuMs - prevCpuMs > epsilonMs;

    internal static IReadOnlyList<int> GetHeartbeatOwnedProcessIds(OwnedProcessGroup? workerGroup, Process? worker)
    {
        if (workerGroup?.TryGetActiveProcessIds(out var activeProcessIds) == true &&
            activeProcessIds.Count > 0)
        {
            return activeProcessIds;
        }

        var processIds = workerGroup?.ProcessIds ?? [];
        if (worker is null)
        {
            return processIds;
        }

        var workerId = worker.Id;
        var descendants = WorkerProcessJobs.ListLiveDescendantProcessIds(workerId);
        if (descendants.Count == 0)
        {
            return processIds;
        }

        return processIds
            .Concat(descendants)
            .Distinct()
            .ToArray();
    }

    internal static IReadOnlyList<SpawnProcessIdentity> CaptureHeartbeatProcessIdentities(
        int hostProcessId,
        IEnumerable<int> ownedProcessIds,
        Func<IReadOnlyList<int>?> readCurrentOwnedProcessIds,
        Func<int, SpawnProcessIdentity?>? readCurrentIdentity = null)
    {
        var identityReader = readCurrentIdentity ?? DispatchProcessIdentityEvidence.ReadCurrent;
        var identities = DispatchProcessIdentityEvidence.Capture(
                ownedProcessIds,
                readCurrentOwnedProcessIds,
                identityReader)
            .ToList();
        var hostIdentity = identityReader(hostProcessId);
        if (hostIdentity is not null)
        {
            identities.RemoveAll(identity => identity.ProcessId == hostProcessId);
            identities.Add(hostIdentity);
        }

        return identities;
    }

    internal static long ReadHeartbeatOwnedCpuMs(OwnedProcessGroup? workerGroup, IReadOnlyList<int> ownedPids)
    {
        return ReadHeartbeatOwnedAccounting(workerGroup)?.CpuMilliseconds ?? SumOwnedCpuMs(ownedPids);
    }

    private static WorkerProcessJobAccounting? ReadHeartbeatOwnedAccounting(OwnedProcessGroup? workerGroup) =>
        workerGroup?.TryReadAccounting(out var accounting) == true ? accounting : null;

    internal static int? SelectHeartbeatChildPid(Process? worker, IReadOnlyList<int> ownedPids)
    {
        if (OperatingSystem.IsWindows() && ownedPids.Count > 0)
        {
            var inspection = WindowsNativeProcessInspection.Read(ownedPids);
            ownedPids = ProcessObservationRoles.CommandCandidates(ownedPids, inspection.Records);
        }
        int? workerId = null;
        var workerRunning = false;
        if (worker is not null)
        {
            workerId = worker.Id;
            try
            {
                workerRunning = !worker.HasExited;
            }
            catch
            {
                workerRunning = false;
            }
        }

        if (ownedPids.Count > 0)
        {
            if (workerId is { } hostPid)
            {
                var nonHostPids = ownedPids
                    .Where(pid => pid != hostPid)
                    .ToArray();
                if (nonHostPids.Length > 0)
                {
                    return nonHostPids
                        .OrderByDescending(ReadProcessCpuMs)
                        .First();
                }

                return workerRunning ? hostPid : null;
            }

            return ownedPids[0];
        }

        return workerRunning ? workerId : null;
    }

    private static long ReadProcessCpuMs(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return (long)process.TotalProcessorTime.TotalMilliseconds;
        }
        catch
        {
            return 0;
        }
    }

    private static long SumOwnedCpuMs(IReadOnlyList<int> processIds)
    {
        var total = 0L;
        foreach (var pid in processIds)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                total += (long)p.TotalProcessorTime.TotalMilliseconds;
            }
            catch
            {
                // Process exited or access denied — contribute 0.
            }
        }
        return total;
    }

    private static TimeSpan ResolveWatchdogTimeout(string environmentVariable, TimeSpan fallback)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var minutes) &&
            minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        return fallback;
    }

    private static void RequireStartGate()
    {
        var gatePath = Environment.GetEnvironmentVariable(StartGatePathVariable);
        if (string.IsNullOrWhiteSpace(gatePath))
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!File.Exists(gatePath) && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        if (!File.Exists(gatePath))
        {
            throw new InvalidOperationException("Dispatch host start gate was not released; refusing to launch worker outside the supervisor job.");
        }
    }

    internal static bool ShouldWritePromptToStdin(DispatchRunParameters parameters) =>
        parameters.Provider is WorkerSandboxProvider.Claude or WorkerSandboxProvider.Codex or WorkerSandboxProvider.Ollama &&
        !string.IsNullOrWhiteSpace(parameters.PromptPath);

    internal static void WritePromptToWorkerStdin(Process worker, DispatchRunParameters parameters)
    {
        try
        {
            if (ShouldWritePromptToStdin(parameters))
            {
                WriteUtf8PromptToStream(parameters.PromptPath!, worker.StandardInput.BaseStream);
                worker.StandardInput.Flush();
            }
        }
        catch (IOException)
        {
            // The worker can exit before the prompt copy finishes. Drain/exit handling records that.
        }
        finally
        {
            // EOF is the end-of-prompt signal for stdin-driven workers. Close before draining output so
            // neither side waits for the other indefinitely.
            try { worker.StandardInput.Close(); } catch { /* worker may have already exited */ }
        }
    }

    internal static void WriteUtf8PromptToStream(string promptPath, Stream target)
    {
        using var source = File.OpenRead(promptPath);
        source.CopyTo(target);
        target.Flush();
    }

    private static void TryWriteExitCode(string path, int exitCode)
    {
        try
        {
            WriteAllTextDurable(path, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            // Best-effort; the orchestrator treats a missing exit file as still-running.
        }
    }

    private static void TryWriteDispatchExitArtifact(string path, int exitCode)
    {
        try
        {
            DispatchExitArtifacts.Write(
                path,
                DispatchExitArtifacts.Native(exitCode, "dispatch host observed worker termination", DateTimeOffset.UtcNow));
        }
        catch
        {
            // Best-effort; the orchestrator treats a missing exit artifact as still-running.
        }
    }

    private static void WriteSandboxPrepEvent(
        DispatchRunParameters parameters,
        string phase,
        DateTimeOffset startedAt,
        TimeSpan? elapsed)
    {
        if (!parameters.SandboxLowIntegrity)
        {
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["event"] = "sandbox-prep",
            ["phase"] = phase,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
            ["startedAt"] = startedAt.ToString("o"),
            ["workingDirectory"] = parameters.WorkingDirectory
        };
        if (elapsed is { } value)
        {
            payload["elapsedMs"] = (long)value.TotalMilliseconds;
        }

        try
        {
            AppendDispatchStderrDiagnostic(parameters.StderrPath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch
        {
            // Sandbox progress is diagnostic only; setup failures are reported separately.
        }
    }

}
