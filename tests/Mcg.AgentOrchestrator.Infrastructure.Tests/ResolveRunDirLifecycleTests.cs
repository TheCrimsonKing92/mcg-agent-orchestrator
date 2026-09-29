using System.Diagnostics;
using System.Runtime.CompilerServices;

public sealed class ResolveRunDirLifecycleTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Missing_files_are_restored_before_reusing_a_published_run(bool holdAppDll)
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var run = first.Stdout.Trim();
            var dependency = Path.Combine(run, "dependency.dll");
            var native = Path.Combine(run, "runtimes", "win-x64", "native", "e_sqlite3.dll");
            File.Delete(dependency);
            File.Delete(native);

            (int ExitCode, string Stdout, string Stderr) repaired;
            using (holdAppDll
                ? File.Open(Path.Combine(run, Path.GetFileName(appDll)), FileMode.Open, FileAccess.Read, FileShare.Read)
                : null)
            {
                repaired = Resolve(root, appDll);
            }
            Assert.Equal(0, repaired.ExitCode);
            Assert.Equal(run, repaired.Stdout.Trim(), ignoreCase: true);
            Assert.Equal("dependency", File.ReadAllText(dependency));
            Assert.Equal("native", File.ReadAllText(native));
            Assert.Equal(0, Resolve(root, appDll).ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Other_digest_is_removed_only_after_its_app_dll_is_released()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var oldRun = first.Stdout.Trim();
            var oldDependency = Path.Combine(oldRun, "dependency.dll");
            File.WriteAllText(Path.Combine(root, "output", "dependency.dll"), "changed dependency");

            using (File.Open(Path.Combine(oldRun, Path.GetFileName(appDll)), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var second = Resolve(root, appDll);
                Assert.Equal(0, second.ExitCode);
                Assert.NotEqual(oldRun, second.Stdout.Trim());
                Assert.Equal("dependency", File.ReadAllText(oldDependency));
                Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(oldRun)!, ".trash-*"));
            }

            var third = Resolve(root, appDll);
            Assert.Equal(0, third.ExitCode);
            Assert.False(Directory.Exists(oldRun));
            Assert.True(Directory.Exists(third.Stdout.Trim()));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(oldRun)!, ".trash-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Missing_marker_is_recreated_and_a_later_resolve_does_not_rewrite_files()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var run = first.Stdout.Trim();
            var marker = Path.Combine(run, ".mcg-run-closure.json");
            var dependency = Path.Combine(run, "dependency.dll");
            File.Delete(marker);
            File.Delete(dependency);

            var repaired = Resolve(root, appDll);
            Assert.Equal(0, repaired.ExitCode);
            Assert.True(File.Exists(marker));
            Assert.Equal("dependency", File.ReadAllText(dependency));
            var writeTime = File.GetLastWriteTimeUtc(dependency);
            Assert.Equal(0, Resolve(root, appDll).ExitCode);
            Assert.Equal(writeTime, File.GetLastWriteTimeUtc(dependency));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task Concurrent_missing_file_repairs_both_accept_the_complete_closure()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var run = first.Stdout.Trim();
            File.Delete(Path.Combine(run, "dependency.dll"));
            File.Delete(Path.Combine(run, ".mcg-run-closure.json"));

            using var start = new ManualResetEventSlim();
            var attempts = Enumerable.Range(0, 2)
                .Select(_ => Task.Run(() => { start.Wait(); return Resolve(root, appDll); }))
                .ToArray();
            start.Set();
            var results = await Task.WhenAll(attempts);

            foreach (var result in results)
            {
                Assert.True(result.ExitCode == 0, result.Stderr);
                Assert.Equal(run, result.Stdout.Trim(), ignoreCase: true);
            }
            Assert.Equal("dependency", File.ReadAllText(Path.Combine(run, "dependency.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Retired_directory_is_reclaimed_only_after_its_owner_process_exits()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var basePath = Path.GetDirectoryName(first.Stdout.Trim())!;
            var orphan = Path.Combine(basePath, $".trash-2147483647-{Guid.NewGuid():N}");
            var active = Path.Combine(basePath, $".trash-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(orphan);
            Directory.CreateDirectory(active);
            File.WriteAllText(Path.Combine(orphan, Path.GetFileName(appDll)), "orphan");
            File.WriteAllText(Path.Combine(active, Path.GetFileName(appDll)), "active");
            File.WriteAllText(Path.Combine(active, "dependency.dll"), "keep whole");

            var second = Resolve(root, appDll);
            Assert.Equal(0, second.ExitCode);
            Assert.False(Directory.Exists(orphan));
            Assert.True(Directory.Exists(active));
            Assert.Equal("active", File.ReadAllText(Path.Combine(active, Path.GetFileName(appDll))));
            Assert.Equal("keep whole", File.ReadAllText(Path.Combine(active, "dependency.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("different-file")]
    [Xunit.InlineData("extra-file")]
    [Xunit.InlineData("nested-marker")]
    [Xunit.InlineData("wrong-marker-digest")]
    public void Invalid_present_content_is_refused_without_mutation(string damage)
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var run = first.Stdout.Trim();
            switch (damage)
            {
                case "different-file":
                    File.WriteAllText(Path.Combine(run, "dependency.dll"), "different");
                    break;
                case "extra-file":
                    File.WriteAllText(Path.Combine(run, "sentinel.txt"), "operator owned");
                    break;
                case "nested-marker":
                    Directory.CreateDirectory(Path.Combine(run, "nested"));
                    File.WriteAllText(Path.Combine(run, "nested", ".mcg-run-closure.json"), "extra");
                    break;
                default:
                    File.WriteAllText(Path.Combine(run, ".mcg-run-closure.json"),
                        "{\"schemaVersion\":2,\"digest\":\"wrong\",\"fileCount\":3}");
                    break;
            }
            var before = Directory.GetFiles(run, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(run, path), File.ReadAllBytes);
            var refused = Resolve(root, appDll);
            Assert.NotEqual(0, refused.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(refused.Stdout), refused.Stdout);
            Assert.Contains("invalid and will not be modified or launched", refused.Stderr);
            var after = Directory.GetFiles(run, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(run, path), File.ReadAllBytes);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var path in before.Keys)
            {
                Assert.Equal(before[path], after[path]);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"resolve-run-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateOutput(string root)
    {
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(Path.Combine(output, "runtimes", "win-x64", "native"));
        var appDll = Path.Combine(output, "Mcg.AgentOrchestrator.App.dll");
        File.WriteAllText(appDll, "app");
        File.WriteAllText(Path.Combine(output, "dependency.dll"), "dependency");
        File.WriteAllText(Path.Combine(output, "runtimes", "win-x64", "native", "e_sqlite3.dll"), "native");
        return appDll;
    }

    private static (int ExitCode, string Stdout, string Stderr) Resolve(string root, string appDll)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = RepositoryRoot(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["TEMP"] = root;
        startInfo.Environment["TMP"] = root;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", "resolve-run-dir.ps1"));
        startInfo.ArgumentList.Add(appDll);
        var result = TestChildProcessCapture.Run(startInfo);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        for (var path = Path.GetDirectoryName(sourceFile); path is not null; path = Path.GetDirectoryName(path))
        {
            if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))
            {
                return path;
            }
        }
        throw new InvalidOperationException("Repository root not found from test source path.");
    }
}
