using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Class-owned, read-only after preparation. Every row gets independent files and kernel state.
public sealed class WorkerBuildScopeDispatchFixtureTemplate : IDisposable
{
    private readonly Lazy<WorkerBuildScopeDispatchFixture> template = new(() => new(),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly List<string> copies = [];

    internal string TemplateMain => template.Value.Main;

    internal WorkerBuildScopeDispatchFixture Copy()
    {
        var source = template.Value;
        var main = Path.Combine(Path.GetDirectoryName(source.Main)!, Guid.NewGuid().ToString("N"));
        copies.Add(main);
        try
        {
            Directory.CreateDirectory(main);
            foreach (var directory in Directory.EnumerateDirectories(source.Main, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Relocate(directory));
            foreach (var file in Directory.EnumerateFiles(source.Main, "*", SearchOption.AllDirectories))
                File.Copy(file, Relocate(file));

            var root = Relocate(source.Root);
            // commondir is relative, so only the two absolute links need repair.
            foreach (var entry in Directory.EnumerateDirectories(Path.Combine(main, ".git", "worktrees")))
            {
                var backlink = Path.Combine(entry, "gitdir");
                var gitFile = Relocate(File.ReadAllText(backlink).Trim());
                File.WriteAllText(backlink, gitFile.Replace('\\', '/') + "\n");
                // Git marks .git hidden on Windows; preserve attributes by truncating in place.
                using var writer = new StreamWriter(File.Open(gitFile, FileMode.Truncate, FileAccess.Write));
                writer.Write("gitdir: " + entry.Replace('\\', '/') + "\n");
            }

            var original = source.Predecessor.LastDispatch!;
            var metadata = JsonSerializer.Deserialize<WorkerBuildProjectScope.Metadata>(
                Convert.FromBase64String(original.WorkerBuildScope!.RetainedMetadataBase64!))!;
            metadata = metadata with
            {
                WorktreeRoot = Relocate(metadata.WorktreeRoot),
                GitCommonDirectory = Relocate(metadata.GitCommonDirectory)
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
            File.WriteAllBytes(WorkerBuildProjectScope.MetadataPath(root), bytes);
            var identity = original.WorkerBuildScope with
            {
                MetadataDigest = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                RetainedMetadataBase64 = Convert.ToBase64String(bytes)
            };
            var dispatch = original with { WorkingDirectory = root, WorkerBuildScope = identity };
            var originalProcess = source.Predecessor.LastProcess!;
            var process = originalProcess with
            {
                WorkingDirectory = root,
                StandardOutputPath = Relocate(originalProcess.StandardOutputPath),
                StandardErrorPath = Relocate(originalProcess.StandardErrorPath),
                ExitCodePath = Relocate(originalProcess.ExitCodePath)
            };
            var receiptPath = Relocate(source.ReceiptPath);
            var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!;
            receipt["worktreeRoot"] = root;
            receipt["scopeMetadataDigest"] = identity.MetadataDigest;
            receipt["scopeStableDigest"] = WorkerBuildScopeStableDigest.Compute(metadata);
            foreach (var result in receipt["projectResults"]!.AsArray())
                result!["logPath"] = Relocate(result["logPath"]!.GetValue<string>());
            File.WriteAllText(receiptPath, receipt.ToJsonString());

            var fixture = new WorkerBuildScopeDispatchFixture(main, root, source, dispatch, process);
            // Detect overlooked path-bearing state without launching git on the recurring copy path.
            foreach (var file in Directory.EnumerateFiles(main, "*", SearchOption.AllDirectories))
            {
                if (Path.GetRelativePath(main, file).Replace('\\', '/').StartsWith(".git/objects/", StringComparison.Ordinal))
                    continue;
                RejectTemplatePath(Encoding.UTF8.GetString(File.ReadAllBytes(file)), file);
            }
            RejectTemplatePath(JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot()), "kernel snapshot");
            return fixture;
        }
        catch
        {
            ScriptSandboxCleanup.DeleteOrThrow([main]);
            throw;
        }

        string Relocate(string path)
        {
            var relative = Path.GetRelativePath(source.Main, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException($"Dispatch template has an external path: {path}");
            return Path.GetFullPath(Path.Combine(main, relative));
        }

        void RejectTemplatePath(string contents, string location)
        {
            var normalized = contents.Replace("\\\\", "\\", StringComparison.Ordinal).Replace('\\', '/');
            if (normalized.Contains(source.Main.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Dispatch template path remains in copy: {location}");
        }
    }

    public void Dispose()
    {
        ScriptSandboxCleanup.DeleteOrThrow(copies);
        if (template.IsValueCreated) template.Value.Dispose();
    }
}
