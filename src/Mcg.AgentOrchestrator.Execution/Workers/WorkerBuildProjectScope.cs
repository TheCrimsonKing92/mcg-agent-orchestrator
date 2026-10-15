using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Selection inputs, never build evidence. One invocation must build the entire returned scope.</summary>
public static class WorkerBuildProjectScope
{
    public const string MetadataRelativePath = ".orchestrator-context/worker-build-scope.json";
    public sealed record Metadata(int Version, string WorktreeRoot, string GitCommonDirectory,
        string Branch, string GoalId, string TaskId, string DispatchIdentity,
        string GoalBaseline, string DispatchBase, string IntegrationBranch);
    public sealed record Result(IReadOnlyList<string> RequestedProjects, IReadOnlyList<string> RequiredProjects,
        IReadOnlyList<string> Projects, string Identity, string MetadataDigest, string StableMetadataDigest);

    public static string MetadataPath(string root) => Path.Combine(root, MetadataRelativePath);

    // The configured integration relationship is resolved once and then preserved across correction rounds.
    public static void Prepare(string root, string goalId, string taskId, string integrationBranch, string? pinnedGoalBaseline = null,
        Action<string>? diagnostic = null)
    {
        root = Canonical(root);
        var branch = Git(root, "symbolic-ref", "--short", "HEAD").Trim();
        var head = Git(root, "rev-parse", "HEAD").Trim();
        var common = Canonical(Git(root, "rev-parse", "--path-format=absolute", "--git-common-dir").Trim());
        var path = MetadataPath(root);
        ValidatePath(root, path, mustExist: false);
        Metadata? previous = File.Exists(path) ? ReadMetadata(root) : null;
        if (previous is not null)
        {
            var squashed = DispatchBaseSquashedOff(root, head, previous);
            // Retain every metadata guard, replacing only the confirmed stale dispatch ancestry.
            ValidateMetadata(root, branch, common, head, squashed ? previous with { DispatchBase = head } : previous);
            // Older worktrees initialized by the explicit operator bridge only know the branch prefix.
            // A tracked dispatch can bind that same root/branch/baseline to its full goal identity once.
            var sameGoal = previous.GoalId == goalId ||
                (previous.TaskId == "legacy-operator" && branch.StartsWith("goal/", StringComparison.OrdinalIgnoreCase) &&
                    previous.GoalId == branch["goal/".Length..] && goalId.StartsWith(previous.GoalId, StringComparison.Ordinal));
            if (!sameGoal || previous.IntegrationBranch != integrationBranch)
                throw new IOException("Worker build scope goal/integration identity changed; conductor must replace the scope explicitly.");
            if (pinnedGoalBaseline is not null && previous.GoalBaseline != pinnedGoalBaseline)
                throw new IOException("Worker build scope baseline differs from the conductor's pinned goal identity.");
            if (squashed)
                (diagnostic ?? Console.Error.WriteLine)($"Worker build scope dispatch base {previous.DispatchBase} is no longer an ancestor of HEAD {head}; replacing the scope.");
        }
        var baseline = pinnedGoalBaseline ?? previous?.GoalBaseline ?? Git(root, "merge-base", head,
            TrunkBranchName.Validate(integrationBranch)).Trim();
        var metadata = new Metadata(1, root, common, branch, goalId, taskId, Guid.NewGuid().ToString("N"),
            baseline, head, integrationBranch);
        ValidateMetadata(root, branch, common, head, metadata);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(metadata), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        static bool DispatchBaseSquashedOff(string root, string head, Metadata previous)
        {
            foreach (var revision in new[] { previous.GoalBaseline, previous.DispatchBase })
                if (revision is null || revision.Length is not (40 or 64) || !revision.All(Uri.IsHexDigit))
                    return false;

            var baseline = GitCli.RunForRepository(root, "merge-base", "--is-ancestor", previous.GoalBaseline, head);
            if (!baseline.Succeeded || baseline.DrainTimedOut) return false;
            var dispatch = GitCli.RunForRepository(root, "merge-base", "--is-ancestor", previous.DispatchBase, head);
            return dispatch.ExitCode == 1 && !dispatch.DrainTimedOut;
        }
    }

    public static WorkerBuildScopeIdentity ReadDispatchIdentity(string root, string? expectedDispatchBase = null)
    {
        ValidatePath(root, MetadataPath(root), mustExist: true);
        var bytes = File.ReadAllBytes(MetadataPath(root));
        var metadata = JsonSerializer.Deserialize<Metadata>(bytes) ?? throw new IOException("Empty worker build scope metadata.");
        if (expectedDispatchBase is not null && metadata.DispatchBase != expectedDispatchBase)
            throw new IOException("Worker build scope dispatch base changed before worker start.");
        ValidateMetadata(Canonical(root), Git(root, "symbolic-ref", "--short", "HEAD").Trim(),
            Canonical(Git(root, "rev-parse", "--path-format=absolute", "--git-common-dir").Trim()),
            Git(root, "rev-parse", "HEAD").Trim(), metadata);
        return new(metadata.GoalBaseline, Hash(bytes));
    }

    internal static string? PinnedGoalBaseline(Goal goal) => goal.Tasks.SelectMany(task => task.DispatchHistory)
        .Select(dispatch => dispatch.WorkerBuildScope?.GoalBaseline).Where(baseline => baseline is not null)
        .Distinct(StringComparer.Ordinal).SingleOrDefault();

    public static Result Resolve(string root, IEnumerable<string> requestedProjects,
        IEnumerable<string>? additionalPaths = null, string? expectedMetadataDigest = null)
    {
        root = Canonical(root);
        if (!SamePath(Canonical(Git(root, "rev-parse", "--show-toplevel").Trim()), root))
            throw new IOException("Worker build root is not the repository root.");
        var branch = Git(root, "symbolic-ref", "--short", "HEAD").Trim();
        var head = Git(root, "rev-parse", "HEAD").Trim();
        var metadataPath = MetadataPath(root);
        var metadataDigest = "none";
        var stableMetadataDigest = "none";
        var baseline = head;
        if (File.Exists(metadataPath))
        {
            ValidatePath(root, metadataPath, mustExist: true);
            var bytes = File.ReadAllBytes(metadataPath);
            metadataDigest = Hash(bytes);
            var metadata = JsonSerializer.Deserialize<Metadata>(bytes) ?? throw new IOException("Empty worker build scope metadata.");
            var common = Canonical(Git(root, "rev-parse", "--path-format=absolute", "--git-common-dir").Trim());
            ValidateMetadata(root, branch, common, head, metadata);
            stableMetadataDigest = WorkerBuildScopeStableDigest.Compute(metadata);
            baseline = metadata.GoalBaseline;
        }
        else if (branch.StartsWith("goal/", StringComparison.OrdinalIgnoreCase) ||
            (File.Exists(Path.Combine(root, ".git")) &&
                root.Replace('\\', '/').Contains("/.orchestrator-worktrees/", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Worker build scope metadata missing for goal worktree. Dispatch must pin the configured integration baseline.");
        if (expectedMetadataDigest is not null && metadataDigest != expectedMetadataDigest)
            throw new IOException("Worker build scope differs from conductor-captured dispatch identity.");

        // No rename heuristics or quoted porcelain paths: no-renames includes both sides as add/delete.
        var paths = Git(root, "diff", "--name-only", "-z", "--no-renames", baseline, "--")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            // Completion also requires dirty/staged owners, even when they restore the goal baseline.
            // Keep index and worktree changes separate: they can cancel each other against HEAD too.
            .Concat(Git(root, "diff", "--name-only", "-z", "--no-renames", "--")
                .Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Concat(Git(root, "diff", "--cached", "--name-only", "-z", "--no-renames", "--")
                .Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Concat(Git(root, "ls-files", "--others", "--exclude-standard", "-z")
                .Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Concat(additionalPaths ?? []);
        var required = WorkerBuildEvidenceRequirement.ResolveRequiredProjects(root, paths);
        if (required.Errors.Count != 0) throw new IOException(string.Join("; ", required.Errors));
        var requested = requestedProjects.Select(project =>
        {
            var path = ValidatePath(root, project, mustExist: true);
            if (!new[] { ".csproj", ".fsproj", ".vbproj" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new IOException($"Not a build project: {project}");
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var projects = requested.Concat(required.Projects).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        var identity = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { root, branch, head, baseline, metadataDigest, requested, required = required.Projects, projects })));
        return new(requested, required.Projects, projects, identity, metadataDigest, stableMetadataDigest);
    }

    internal static string ValidatePath(string root, string path, bool mustExist)
    {
        root = Canonical(root);
        var absolute = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, absolute);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException($"Project/source path outside worker worktree: {path}");
        for (var current = absolute; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Redirected worker build path: {current}");
        }
        if (mustExist && !File.Exists(absolute)) throw new IOException($"Project/input missing: {path}");
        return absolute;
    }

    private static Metadata ReadMetadata(string root) => JsonSerializer.Deserialize<Metadata>(File.ReadAllBytes(MetadataPath(root)))
        ?? throw new IOException("Empty worker build scope metadata.");

    private static void ValidateMetadata(string root, string branch, string common, string head, Metadata metadata)
    {
        if (metadata.Version != 1 || !SamePath(Canonical(metadata.WorktreeRoot), root) ||
            !SamePath(Canonical(metadata.GitCommonDirectory), common) || metadata.Branch != branch ||
            string.IsNullOrWhiteSpace(metadata.GoalId) || string.IsNullOrWhiteSpace(metadata.TaskId) ||
            string.IsNullOrWhiteSpace(metadata.DispatchIdentity) || string.IsNullOrWhiteSpace(metadata.IntegrationBranch) ||
            (branch.StartsWith("goal/", StringComparison.OrdinalIgnoreCase) &&
                !metadata.GoalId.StartsWith(branch[5..], StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Foreign or malformed worker build scope metadata.");
        foreach (var revision in new[] { metadata.GoalBaseline, metadata.DispatchBase })
        {
            if (revision is null || revision.Length is not (40 or 64) || !revision.All(Uri.IsHexDigit))
                throw new IOException("Worker build scope requires immutable commit identities.");
            Git(root, "merge-base", "--is-ancestor", revision, head);
        }
        Git(root, "merge-base", "--is-ancestor", metadata.GoalBaseline, metadata.DispatchBase);
    }

    private static string Git(string root, params string[] arguments)
    {
        var result = GitCli.RunForRepository(root, arguments);
        if (!result.Succeeded || result.DrainTimedOut)
            throw new IOException($"Worker build scope Git {arguments[0]} failed (exit={result.ExitCode}): {result.Error.Trim()}");
        return result.Output;
    }
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
