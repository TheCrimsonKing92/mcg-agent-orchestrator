using System.IO.Compression;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ExecutorToolsSource(string MainSha, string Folder, string Digest);

// The deployed source is always the integration branch's tracked bytes.
internal static class ExecutorToolsMainSource
{
    internal static ExecutorToolsSource? Materialize(string executionDirectory, string integrationBranch,
        string hostStateRoot, Func<string, int, string[], GitCli.GitResult> git)
    {
        string? zip = null;
        string? temporary = null;
        try
        {
            var revision = git(executionDirectory, 60_000, ["rev-parse", integrationBranch]);
            var sha = revision.Output.Trim();
            if (revision.ExitCode != 0 || sha.Length != 40 || !sha.All(char.IsAsciiHexDigit)) return null;
            var root = Path.Combine(hostStateRoot, ".orchestrator", "executor-tools-deploy");
            Directory.CreateDirectory(root);
            var record = Path.Combine(root, "main-source.json");
            if (File.Exists(record))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<ExecutorToolsSource>(File.ReadAllText(record));
                    if (cached is not null && cached.MainSha == sha && cached.Digest.Length == 64 &&
                        cached.Digest.All(char.IsAsciiHexDigit))
                    {
                        var folder = Path.Combine(root, "tools-staging-" + cached.Digest[..12]);
                        if (ExecutorToolsDigest.Compute(folder) == cached.Digest) return cached with { Folder = folder };
                    }
                }
                catch (Exception) { /* A broken cache is rebuilt from tracked source. */ }
            }
            zip = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
            if (git(executionDirectory, 60_000,
                ["archive", "--format=zip", "-o", zip, sha, "scripts/executor/tools"]).ExitCode != 0) return null;
            temporary = Path.Combine(root, "tools-staging-tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            using (var archive = ZipFile.OpenRead(zip))
            {
                const string prefix = "scripts/executor/tools/";
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var name = entry.FullName[prefix.Length..];
                    if (name.Length == 0 || name.Contains('/') || name.Contains('\\') ||
                        name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                        !name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) continue;
                    entry.ExtractToFile(Path.Combine(temporary, name));
                }
            }
            var digest = ExecutorToolsDigest.Compute(temporary);
            if (digest is null) return null;
            var destination = Path.Combine(root, "tools-staging-" + digest[..12]);
            if (Directory.Exists(destination) && ExecutorToolsDigest.Compute(destination) != digest)
                Directory.Delete(destination, recursive: true);
            if (!Directory.Exists(destination)) Directory.Move(temporary, destination);
            var source = new ExecutorToolsSource(sha, destination, digest);
            var pending = record + ".tmp";
            File.WriteAllText(pending, JsonSerializer.Serialize(source));
            File.Move(pending, record, overwrite: true);
            return source;
        }
        catch (Exception) { return null; }
        finally
        {
            try { if (zip is not null) File.Delete(zip); } catch (Exception) { }
            try { if (temporary is not null && Directory.Exists(temporary)) Directory.Delete(temporary, true); }
            catch (Exception) { }
        }
    }
}
