using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class OwnedProcessExitObservationTestsLongCapturePath
{
    [Xunit.Fact]
    public void StartSuspendedWithFileCapture_OpensLongPaths()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var root = Path.Combine(Path.GetTempPath(), "mcg-long-capture-" + Guid.NewGuid().ToString("N"));
        var directory = root;
        while (Path.Combine(directory, "stdout.log").Length <= 300)
        {
            directory = Path.Combine(directory, new string('p', 40));
        }

        var stdoutPath = Path.Combine(directory, "stdout.log");
        var stderrPath = Path.Combine(directory, "stderr.log");
        Xunit.Assert.True(stdoutPath.Length > 260);
        Xunit.Assert.True(stderrPath.Length > 260);

        try
        {
            Directory.CreateDirectory(directory);
            using var launch = OwnedProcessGroup.StartSuspendedWithFileCapture(
                new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/d /c echo long-capture",
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                stdoutPath,
                stderrPath);
            launch.Resume();
            Xunit.Assert.True(launch.WaitForOwnedExit(TimeSpan.FromSeconds(30)));
            Xunit.Assert.Contains("long-capture", File.ReadAllText(stdoutPath));
            Xunit.Assert.True(File.Exists(stderrPath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
