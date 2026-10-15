namespace Mcg.AgentOrchestrator.App.Orchestration;

// An invocation's expectations, not a second workspace registry or ambient override.
internal sealed record WorkspaceInvocationBinding(
    string? RepositoryRoot, string? ExpectedStateDirectory, bool DisposableFixture,
    IReadOnlyList<string> CommandArgs)
{
    internal bool RequireCurrentStores { get; init; }
    internal static WorkspaceInvocationBinding Parse(IReadOnlyList<string> args)
    {
        string? root = null, state = null;
        var fixture = false;
        var requireCurrent = false;
        var remaining = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--require-current-stores", StringComparison.OrdinalIgnoreCase))
            {
                requireCurrent = true;
                continue;
            }
            if (arg.Equals("--disposable-fixture", StringComparison.OrdinalIgnoreCase))
            {
                if (fixture) throw new ArgumentException("Duplicate --disposable-fixture.");
                fixture = true;
                continue;
            }
            var name = arg.Split('=', 2)[0].ToLowerInvariant();
            if (name is not ("--repository-root" or "--expected-state-directory"))
            {
                remaining.Add(arg);
                continue;
            }
            var value = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] :
                ++i < args.Count ? args[i] : throw new ArgumentException($"Missing value for {name}.");
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                throw new ArgumentException($"{name} requires an absolute path.");
            if (name == "--repository-root")
            {
                if (root is not null) throw new ArgumentException($"Duplicate {name}.");
                root = CanonicalPath(value);
            }
            else
            {
                if (state is not null) throw new ArgumentException($"Duplicate {name}.");
                state = CanonicalPath(value);
            }
        }
        if (fixture && (root is null || state is null))
            throw new InvalidOperationException("Disposable fixture requires --repository-root and --expected-state-directory before startup.");
        if (root is not null && !Directory.Exists(root))
            throw new InvalidOperationException($"Repository root does not exist: {root}.");
        return new(root, state, fixture, remaining) { RequireCurrentStores = requireCurrent };
    }

    internal void ValidateInheritedSelectors(string? inheritedRoot,
        string? inheritedProject, string? inheritedTenant,
        OrchestratorProjectSelection project, OrchestratorTenantSelection tenant)
    {
        if (!DisposableFixture) return;
        if (!string.IsNullOrWhiteSpace(inheritedRoot) && !SamePath(inheritedRoot, RepositoryRoot!))
            throw new InvalidOperationException("Disposable fixture conflicts with inherited repository root.");
        if (!string.IsNullOrWhiteSpace(inheritedProject) &&
            !string.Equals(inheritedProject, project.ProjectName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Disposable fixture conflicts with inherited project selector.");
        if (!string.IsNullOrWhiteSpace(inheritedTenant) &&
            !string.Equals(inheritedTenant, tenant.TenantName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Disposable fixture conflicts with inherited tenant selector.");
    }

    internal void Verify(OrchestratorWorkspace workspace)
    {
        if (RepositoryRoot is not null && !SamePath(RepositoryRoot, workspace.RootDirectory))
            throw new InvalidOperationException($"Resolved repository '{workspace.RootDirectory}' differs from explicit root '{RepositoryRoot}'.");
        if (ExpectedStateDirectory is not null && !SamePath(ExpectedStateDirectory, workspace.OrchestratorDirectory))
            throw new InvalidOperationException($"Resolved state directory '{workspace.OrchestratorDirectory}' differs from expected '{ExpectedStateDirectory}'.");
        if (RepositoryRoot is null && ExpectedStateDirectory is null) return;
        // Refuse aliases rather than trusting textual prefix comparisons through junctions/symlinks.
        VerifyPhysicalPath(workspace.RootDirectory);
        VerifyPhysicalPath(workspace.ExecutionDirectory);
        VerifyPhysicalPath(workspace.OrchestratorDirectory);
        foreach (var database in Mcg.AgentOrchestrator.Infrastructure.StoreSchemaRegistry.Inventory.Select(entry => entry.Database).Distinct())
            VerifyPhysicalPath(Path.Combine(workspace.OrchestratorDirectory, database));
        VerifyPhysicalPath(workspace.LogDirectory);
    }

    internal static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static bool SamePath(string left, string right) => string.Equals(
        CanonicalPath(left), CanonicalPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static void VerifyPhysicalPath(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Workspace binding refuses a filesystem alias: {directory.FullName}.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Workspace binding refuses a filesystem alias: {path}.");
    }
}
