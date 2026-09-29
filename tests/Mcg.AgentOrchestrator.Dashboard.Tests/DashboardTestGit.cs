using System.Diagnostics;

internal static class DashboardTestGit
{
    private static readonly Lazy<string> EmptyGlobalConfig = new(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-dashboard-git");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "empty-global.gitconfig");
        File.WriteAllText(path, string.Empty);
        return path;
    });

    internal static string Run(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
        {
            if (startInfo.Environment.TryGetValue(variable, out var value) && !string.IsNullOrWhiteSpace(value))
                allowed[variable] = value;
        }
        startInfo.Environment.Clear();
        foreach (var (name, value) in allowed) startInfo.Environment[name] = value;
        foreach (var variable in new[]
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_COMMON_DIR"
        }) startInfo.Environment.Remove(variable);
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = EmptyGlobalConfig.Value;
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";

        foreach (var argument in new[]
        {
            "-c", "core.fsmonitor=false", "-c", "core.longpaths=true",
            "-c", "gc.auto=0", "-c", "maintenance.auto=false"
        }) startInfo.ArgumentList.Add(argument);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            string termination;
            try
            {
                process.Kill(entireProcessTree: true);
                termination = process.WaitForExit(5_000)
                    ? "process tree terminated"
                    : "process tree did not exit within 5 seconds after termination";
            }
            catch (InvalidOperationException) { termination = "process exited before termination"; }
            catch (System.ComponentModel.Win32Exception ex) { termination = $"process termination failed: {ex.Message}"; }
            var timedOutOutput = outputTask.IsCompletedSuccessfully ? outputTask.Result : "<stream still open>";
            var timedOutError = errorTask.IsCompletedSuccessfully ? errorTask.Result : "<stream still open>";
            throw new TimeoutException(
                $"git {string.Join(' ', arguments)} did not exit within 30 seconds; {termination}: stdout={timedOutOutput} stderr={timedOutError}");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit {process.ExitCode}: {output}{error}");
        return output;
    }
}
