using System.Collections.Immutable;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleBoardProject(string Name, string StatePath, string PortfolioPath,
    string? Error = null);
internal sealed record OwnerConsoleBoardScope(string Label, int ProjectCount, string? DisclosureWorkspace);
internal sealed record OwnerConsoleScopeCommandResult(string Message, bool Success, bool Changed);

// Owns BOARD-only reads. Foreign builders and their goal-id caches never escape a refresh.
internal sealed class OwnerConsoleBoardSource(string launchProject, OwnerConsoleProjectScope initialScope,
    Func<IReadOnlyList<OwnerConsoleBoardProject>> catalog, TimeProvider clock)
{
    private readonly object _sync = new();
    private OwnerConsoleProjectScope _scope = initialScope;

    internal static OwnerConsoleBoardSource FromRegistry(OrchestratorWorkspace workspace,
        string repositoryRoot, OrchestratorProjectRegistry registry, TimeProvider clock) =>
        new(OwnerConsoleProjectLabel.ForWorkspace(workspace), OwnerConsoleProjectScope.ForLaunch(workspace), () =>
        {
            var projects = new List<OwnerConsoleBoardProject>();
            OwnerConsoleBoardProject Describe(string name, Func<OrchestratorWorkspace> resolve)
            {
                try
                {
                    var target = name.Equals(OwnerConsoleProjectLabel.ForWorkspace(workspace), StringComparison.OrdinalIgnoreCase)
                        ? workspace : resolve();
                    return new(name, target.SqliteStatePath, target.PortfolioStorePath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { return new(name, "", "", ex.Message); }
            }
            projects.Add(Describe("home", () => OrchestratorWorkspace.ForDirectory(repositoryRoot, null, workspace.TenantName, registry)));
            foreach (var project in registry.ListProjects())
            {
                if (project.Name.Equals("home", StringComparison.OrdinalIgnoreCase)) continue;
                projects.Add(Describe(project.Name, () => OrchestratorWorkspace.ForProject(project.Name,
                    project.RootDirectory, tenantName: workspace.TenantName, integrationBranch: project.IntegrationBranch,
                    dataRootDirectory: registry.DataRootDirectory)));
            }
            return projects;
        }, clock);

    internal OwnerConsoleScopeCommandResult HandleScope(string? argument)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(argument)) return new($"scope: {_scope.Label}", true, false);
            try
            {
                var known = argument.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? Array.Empty<string>() : Ordered(catalog()).Select(project => project.Name).ToArray();
                var scope = OwnerConsoleProjectScope.Parse(argument, known, out var error);
                if (scope is null) return new(error!, false, false);
                var changed = !_scope.SameAs(scope);
                _scope = scope;
                return new($"scope: {scope.Label}", true, changed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return new($"scope: could not read projects: {ex.Message}", false, false); }
        }
    }

    internal async Task<OwnerConsoleViewModel> ComposeAsync(OwnerConsoleViewModel own,
        CancellationToken token = default)
    {
        OwnerConsoleProjectScope scope;
        lock (_sync) scope = _scope;
        var rows = new List<OwnerConsoleBoardRow>();
        var resolved = new List<string>();
        try
        {
            var projects = Ordered(catalog()).ToArray();
            foreach (var name in scope.IsAll ? projects.Select(project => project.Name) : scope.Names)
            {
                token.ThrowIfCancellationRequested();
                var project = projects.FirstOrDefault(project => project.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var isOwn = name.Equals(launchProject, StringComparison.OrdinalIgnoreCase);
                try
                {
                    if (project?.Error is { } error) throw new IOException(error);
                    // GetAttributes distinguishes confirmed absence from inaccessible state.
                    if (project is null || !Exists(project.StatePath))
                    {
                        if (scope.IsAll && !name.Equals("home", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!isOwn) throw new FileNotFoundException("state database was not found");
                    }
                    resolved.Add(name);
                    if (isOwn)
                        rows.AddRange(own.Board.Select(row => row with { Project = name }));
                    else
                    {
                        var state = SqliteOrchestratorStateRepository.OpenReadOnly(project!.StatePath);
                        var builder = new OwnerConsoleViewModelBuilder(state, new NoQuestions(), new NoLiveness(),
                            new ReadOnlyEpics(project.PortfolioPath), clock);
                        var model = await builder.BuildBoardAsync(token);
                        rows.AddRange(model.Board.Select(row => row with { Project = name, IsForeign = true }));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!resolved.Contains(name, StringComparer.OrdinalIgnoreCase)) resolved.Add(name);
                    rows.Add(Diagnostic(name, ex));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            resolved.Add(launchProject);
            rows.AddRange(own.Board.Select(row => row with { Project = launchProject }));
            rows.Add(Diagnostic("registry", ex));
        }
        token.ThrowIfCancellationRequested();
        return own with
        {
            Board = rows.OrderBy(row => row.IsDiagnostic).ThenBy(row => row.SortKey)
                .ThenBy(row => row.Project, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.GoalId, StringComparer.Ordinal).ToImmutableArray(),
            BoardScope = new(scope.Label, resolved.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                resolved.Any(name => !name.Equals(launchProject, StringComparison.OrdinalIgnoreCase)) ? launchProject : null)
        };
    }

    internal static string ForeignRowMessage(OwnerConsoleBoardRow row) =>
        $"{row.GoalPrefix} belongs to {row.Project}. Detail and actions for other projects arrive in a later slice; open it with --project={(row.Project.Equals("home", StringComparison.OrdinalIgnoreCase) ? "default" : row.Project)} console.";

    private static IEnumerable<OwnerConsoleBoardProject> Ordered(IReadOnlyList<OwnerConsoleBoardProject> projects) =>
        projects.DistinctBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project.Name.Equals("home", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase);

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static OwnerConsoleBoardRow Diagnostic(string name, Exception error) =>
        new("", "", $"{name}: state read failed: {error.Message.ReplaceLineEndings(" ")}", "", "", null, "", Dimmed: true)
        { Project = name, IsDiagnostic = true };

    private sealed class NoQuestions : IOwnerQuestionSource
    {
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OwnerQuestion>>([]);
    }
    private sealed class NoLiveness : IConductorLiveness { public bool IsRunning() => false; }
    private sealed class ReadOnlyEpics(string path) : IOwnerGoalEpicLookup
    {
        public async Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken)
        {
            try { return (await PortfolioStore.OpenReadOnly(path).GetGoalMembershipAsync(goalId, cancellationToken))?.EpicTitle ?? ""; }
            catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
        }
    }
}
