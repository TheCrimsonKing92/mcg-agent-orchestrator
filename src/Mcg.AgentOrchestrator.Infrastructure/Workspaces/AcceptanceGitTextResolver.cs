using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceGitTextResolver
{
    internal static string? Resolve(string worktreePath, params string[] arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = worktreePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return null;
            }

            // Drain both pipes concurrently BEFORE waiting for exit. Waiting first deadlocks as soon
            // as the child's output exceeds the pipe buffer (~4KB): the child blocks writing, the
            // 5s wait expires, and the caller sees null. The acceptance manifest crossed that size
            // when the engine section landed, which turned every trusted-manifest read into a refusal.
            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                try { process.WaitForExit(1000); } catch { /* best effort */ }
                try { Task.WhenAll(standardOutputTask, standardErrorTask).Wait(1000); } catch { /* best effort */ }
                return null;
            }

            Task.WhenAll(standardOutputTask, standardErrorTask).GetAwaiter().GetResult();
            return process.ExitCode == 0 ? standardOutputTask.Result : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
