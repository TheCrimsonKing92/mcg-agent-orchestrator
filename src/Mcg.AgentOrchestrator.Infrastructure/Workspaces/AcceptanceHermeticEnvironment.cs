using static Mcg.AgentOrchestrator.Infrastructure.AcceptanceTempRootNames;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class AcceptanceHermeticEnvironment
{
    // The acceptance suite verifies the CODE and must run hermetically — NOT under whatever runtime config
    // the operator's conductor happened to be launched with. Anything surviving here is an ambient input the
    // gate silently depends on, which makes its verdict a statement about the launcher. Proven leaks include:
    //
    // 1. Worker-dispatch vars (MCG_WORKER_SANDBOX and friends) control how real workers launch (low
    //    integrity). Several tests read WorkerSandboxOptions.FromEnvironment(), so running `conduct` with
    //    MCG_WORKER_SANDBOX=1 flipped those tests' expected sandbox mode — failing acceptance INSIDE the
    //    watch while the same suite passed when `acceptance` ran standalone.
    // 2. Handoff coordination vars made CLI grandchildren skip startup cleanup, masking a real backlog-list
    //    regression. Acceptance-scope overrides then demonstrated why extending that deny-list is insufficient:
    //    either override could bypass the build-system-file rule and false-green its regression test.
    // Identity for commits a verification child creates (the pre-landing rebase replays commits). Fixed and
    // attributable on purpose: a gate must not author commits as the operator, and it must not depend on the
    // operator having configured git at all.
    private const string HermeticGitIdentityName = "MCG Acceptance Gate";
    private const string HermeticGitIdentityEmail = "acceptance-gate@localhost";

    public static void ConfigureHermeticVerificationEnvironment(
        IDictionary<string, string?> environment,
        string repositoryRoot,
        string? buildEnvironmentRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        environment.TryGetValue(NuGetPackagesVariable, out var nugetPackages);

        var allowed = HermeticSystemDrive.WithResolvedSystemDrive(environment
            .Where(pair => IsInheritedHermeticVerificationEnvironmentVariable(pair.Key))
            .ToArray(), Environment.SystemDirectory);

        // This is deliberately an allow-list, not another enumeration of known-dangerous inputs. Verification
        // children have accumulated behavior switches, provider credentials, control-plane tokens, and attempt
        // identity outside MCG_*; missing any one of them can turn a real failure green or emit real traffic.
        environment.Clear();
        foreach (var pair in allowed)
        {
            environment[pair.Key] = pair.Value;
        }

        var profileRoot = Path.Combine(Path.GetTempPath(), HermeticProfileRootDirectoryName);
        Directory.CreateDirectory(profileRoot);
        var dotnetCliHome = string.IsNullOrWhiteSpace(buildEnvironmentRoot)
            ? profileRoot : Path.Combine(buildEnvironmentRoot, "dotnet-cli-home");
        Directory.CreateDirectory(dotnetCliHome);
        nugetPackages = AcceptanceTempRootNames.ResolveNuGetPackagesFolder(nugetPackages);
        environment["HOME"] = profileRoot;
        environment["USERPROFILE"] = profileRoot;
        environment["DOTNET_CLI_HOME"] = dotnetCliHome;

        // Git resolves user.name/user.email from $HOME/.gitconfig, and the profile root above is empty, so a
        // verification child inherits NO GIT IDENTITY. Read-only git is unaffected, which is why this hid for
        // so long - but the pre-landing rebase REPLAYS COMMITS, and git exits 128 ("unable to auto-detect
        // email address") the moment it needs an identity. That surfaced as
        // "Rebase of goal/X onto main failed ... (exit=128; stderr=Rebasing (1/3)" on every landing attempt:
        // git printed its progress, hit the first commit, and died. A manual rebase always worked because an
        // operator shell still has the real HOME.
        //
        // Supply a DETERMINISTIC identity rather than borrowing the operator's global config. Pointing
        // GIT_CONFIG_GLOBAL at the real ~/.gitconfig would restore identity but reintroduce exactly the
        // ambient-input class this function exists to forbid - any [core], [merge], [rebase] or [alias] the
        // operator later adds would silently steer gate behaviour - and it would still miss a host that keeps
        // identity under $XDG_CONFIG_HOME. These four variables outrank every config file, so identity is
        // guaranteed regardless of where the operator's happens to live. Same shape as the canary's inline
        // -c user.name/-c user.email.
        environment["GIT_AUTHOR_NAME"] = HermeticGitIdentityName;
        environment["GIT_AUTHOR_EMAIL"] = HermeticGitIdentityEmail;
        environment["GIT_COMMITTER_NAME"] = HermeticGitIdentityName;
        environment["GIT_COMMITTER_EMAIL"] = HermeticGitIdentityEmail;
        AcceptanceHermeticGitConfiguration.Configure(environment, profileRoot);
        // A relocated DOTNET_CLI_HOME reads as a first use, and the SDK responds by PERSISTING
        // "$DOTNET_CLI_HOME\.dotnet\tools" into the operator's HKCU\Environment\Path. That is ambient
        // state written by a function whose entire purpose is to write none, so it must be suppressed
        // here rather than cleaned up afterwards. DOTNET_SKIP_FIRST_TIME_EXPERIENCE does NOT suppress
        // it - measured on .NET 10, the entry is still written; the SDK dropped that variable years ago.
        environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        environment["DOTNET_NOLOGO"] = "1";
        // Acceptance tests recursively invoke dotnet/MSBuild. Reusing a daemon that was born outside this
        // hermetic process tree loses both the gate's environment and its Windows error-mode suppression,
        // allowing a nested test apphost startup failure to block the desktop with a modal error dialog.
        environment["MSBUILDDISABLENODEREUSE"] = "1";
        environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        environment[NuGetPackagesVariable] = nugetPackages;
        if (!string.IsNullOrWhiteSpace(buildEnvironmentRoot))
        {
            // NUGET_PACKAGES isolates restored packages, but NuGet's HTTP cache is derived from
            // LOCALAPPDATA on Windows. Verification deliberately preserves the real LOCALAPPDATA, so
            // concurrent arms otherwise race on %LOCALAPPDATA%\NuGet\v3-cache (notably vuln_index.dat-new).
            // Keep the cache inside the build lease so candidate and baseline restores cannot share locks,
            // and so invocation-local baseline cleanup removes it with the rest of the build environment.
            var nugetHttpCachePath = Path.Combine(buildEnvironmentRoot, "nuget-http-cache");
            Directory.CreateDirectory(nugetHttpCachePath);
            environment["NUGET_HTTP_CACHE_PATH"] = nugetHttpCachePath;
        }
        if (OperatingSystem.IsWindows())
        {
            var profileRootPath = Path.GetPathRoot(profileRoot) ?? string.Empty;
            // APPDATA/LOCALAPPDATA stay pinned to the REAL per-user locations, and are set explicitly rather
            // than left to derive from the repointed USERPROFILE above. Moving them into the profile broke
            // three different things in one day, each in its own way, because everything Windows derives from
            // LOCALAPPDATA moved with it: the per-user temp location (%LOCALAPPDATA%\Temp) vanished, paths
            // built through it outgrew MAX_PATH and failed git object writes, and the PowerShell 7 execution
            // alias under %LOCALAPPDATA%\Microsoft\WindowsApps stopped resolving so callers silently degraded
            // to Windows PowerShell 5.1. Isolation of credentials and caches is already achieved by HOME,
            // USERPROFILE, DOTNET_CLI_HOME and NUGET_PACKAGES; relocating LOCALAPPDATA added no isolation the
            // others do not, and its blast radius is every path anything derives from it.
            // This function MUST BE IDEMPOTENT, because it runs at two nested levels: the conductor
            // configures the __acceptance-gate-attempt child, and that child - already living inside the
            // hermetic environment, with USERPROFILE repointed at the profile root - configures each lane in
            // turn. GetFolderPath expands the REG_EXPAND_SZ literal "%USERPROFILE%\AppData\Local" against the
            // CALLING process, so at the inner level it no longer returns the real per-user folder: it
            // returns <profile-root>\AppData\Local, and the lanes inherited THAT as their LOCALAPPDATA. Every
            // path derived from it then nested one level deeper per hop, which is where the observed
            // ...\mcg-hvp\AppData\Local\Temp\Low\mcg-tests came from, and it is why gate lanes kept resolving
            // Windows PowerShell 5.1 instead of the per-user pwsh 7 install even after WorkerShell learned to
            // read the variable - the variable it read was already redirected.
            //
            // Prefer the INHERITED variable, which the outer level pinned to the real location, and fall back
            // to the known folder only at the outermost level where no variable exists yet.
            var (appData, localAppData) = ResolvePerUserFolders(
                Environment.GetEnvironmentVariable("APPDATA"),
                Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            // Repointing LOCALAPPDATA silently moves every path DERIVED from it, and the Windows per-user
            // temp location is %LOCALAPPDATA%\Temp. Callers that resolve their own temp root that way - the
            // test assembly's temp redirect does exactly this - then land under a directory that exists only
            // if we make it. Leaving it absent produced
            // "unable to write file ...\mcg-hvp\AppData\Local\Temp" and failed
            // whole lanes on environment construction rather than on the code being verified. The parent
            // dirs above are created for the same reason; this is the one that was missed.
            Directory.CreateDirectory(Path.Combine(localAppData, "Temp"));
            environment["HOMEDRIVE"] = profileRootPath.TrimEnd(Path.DirectorySeparatorChar);
            environment["HOMEPATH"] = Path.DirectorySeparatorChar +
                profileRoot[profileRootPath.Length..].TrimStart(Path.DirectorySeparatorChar);
            environment["APPDATA"] = appData;
            environment["LOCALAPPDATA"] = localAppData;
            HermeticToolCacheEnvironment.Apply(environment, profileRoot);
        }

        environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = Path.GetFullPath(repositoryRoot);
    }

    /// <summary>
    /// Chooses the per-user APPDATA/LOCALAPPDATA this environment should carry, preferring the INHERITED
    /// variables over the known-folder API. Pure so the nested-level behaviour is assertable: the known-folder
    /// values cannot be varied in-process (Windows resolves them from the token, not from a mutated
    /// USERPROFILE), so a test that tried to simulate nesting by setting environment variables would pass
    /// with or without the fix.
    /// </summary>
    internal static (string AppData, string LocalAppData) ResolvePerUserFolders(
        string? appDataVariable,
        string? localAppDataVariable,
        string appDataKnownFolder,
        string localAppDataKnownFolder) =>
        (string.IsNullOrWhiteSpace(appDataVariable) ? appDataKnownFolder : appDataVariable,
         string.IsNullOrWhiteSpace(localAppDataVariable) ? localAppDataKnownFolder : localAppDataVariable);

    internal static bool IsHermeticVerificationEnvironmentVariable(string name) =>
        IsInheritedHermeticVerificationEnvironmentVariable(name) ||
        name.Equals("HOME", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HOMEDRIVE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HOMEPATH", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("APPDATA", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PSModuleAnalysisCachePath", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MSBUILDDISABLENODEREUSE", StringComparison.OrdinalIgnoreCase) ||
        // The four deterministic git identity variables this function sets. They are DECLARED here rather
        // than inherited: the allow-list is what the hermetic environment is permitted to contain, so every
        // variable ConfigureHermeticVerificationEnvironment writes must be nameable here or the gate's own
        // hermeticity assertion fails on it.
        name.Equals("GIT_AUTHOR_NAME", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_AUTHOR_EMAIL", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_COMMITTER_NAME", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_COMMITTER_EMAIL", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_CONFIG_NOSYSTEM", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_CONFIG_GLOBAL", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_TERMINAL_PROMPT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GCM_INTERACTIVE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, StringComparison.OrdinalIgnoreCase) || name.Equals(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(TempRootApparatusLossReceiptStore.GateInvocationIdVariable, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(TempRootApparatusLossReceiptStore.ReceiptPathVariable, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MCG_ORCHESTRATOR_REPOSITORY_ROOT", StringComparison.OrdinalIgnoreCase) || GoToolCacheEnvironment.IsVariable(name);

    private static bool IsInheritedHermeticVerificationEnvironmentVariable(string name) =>
        IsHermeticSystemLocationVariable(name) ||
        name.Equals("PATH", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WINDIR", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("COMSPEC", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ProgramFiles", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ProgramFiles(x86)", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ProgramW6432", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TMP", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TMPDIR", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("NUGET_", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] HermeticSystemLocationVariables =
    [
        "SystemDrive",
        "ProgramData",
        "ALLUSERSPROFILE",
        "PUBLIC",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "CommonProgramW6432"
    ];

    private static bool IsHermeticSystemLocationVariable(string name) =>
        HermeticSystemLocationVariables.Contains(name, StringComparer.OrdinalIgnoreCase);
}
