using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record HostExclusionSnapshot(bool CanRead, IReadOnlyList<string> Paths);

public interface IHostExclusionReader
{
    bool IsElevated { get; }
    HostExclusionSnapshot Read();
}

public interface IHostExclusionWriter
{
    void AddExclusionPath(string path);
}

/// <summary>Used only by the explicit host-exclusions CLI command.</summary>
public sealed class DefenderPreferenceCmdletAdapter : IHostExclusionReader, IHostExclusionWriter
{
    public bool IsElevated => Environment.IsPrivilegedProcess;

    public HostExclusionSnapshot Read()
    {
        RequireWindows();
        if (!IsElevated) return new(false, []);
        var paths = JsonSerializer.Deserialize<string[]>(ExecuteAsync("Read", null).GetAwaiter().GetResult())
            ?? throw new InvalidOperationException("Defender returned no exclusion list.");
        if (paths.Any(path => path.StartsWith("N/A:", StringComparison.OrdinalIgnoreCase)))
            return new(false, []);
        return new(true, paths);
    }

    public void AddExclusionPath(string path)
    {
        RequireWindows();
        if (!IsElevated) throw new InvalidOperationException("Run elevated to add Defender exclusions.");
        ExecuteAsync("Add", path).GetAwaiter().GetResult();
    }

    // -Command reparses trailing arguments as script. A fixed -File script binds each path as data.
    internal static ProcessStartInfo CreateStartInfo(string operation, string? path, string scriptPath)
    {
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", scriptPath, "-Operation", operation })
            start.ArgumentList.Add(argument);
        if (path is not null)
        {
            start.ArgumentList.Add("-ExclusionPath");
            start.ArgumentList.Add(path);
        }
        return start;
    }

    private static async Task<string> ExecuteAsync(string operation, string? path)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "DefenderExclusionPreferences.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Defender adapter script is missing.", script);
        using var process = new Process { StartInfo = CreateStartInfo(operation, path, script) };
        if (!process.Start()) throw new InvalidOperationException("Could not start Defender preference reader/writer.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr)
                .WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            throw new TimeoutException("Defender preference command timed out after 60 seconds.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Defender preference command exited {process.ExitCode}: {await stderr.ConfigureAwait(false)}");
        return await stdout.ConfigureAwait(false);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("host-exclusions requires Windows Defender on Windows.");
    }
}
