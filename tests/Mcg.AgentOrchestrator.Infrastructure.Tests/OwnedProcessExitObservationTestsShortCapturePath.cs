using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class OwnedProcessExitObservationTestsShortCapturePath
{
    [Xunit.Fact]
    public void StartSuspendedWithFileCapture_PreservesShortPathOutputAndNames()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var tempRoot = Path.GetTempPath();
        if (Path.Combine(tempRoot, "mcg-sc-12345678", "stdout.log").Length >= 100)
        {
            tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        }

        var directory = Path.Combine(tempRoot, "mcg-sc-" + Guid.NewGuid().ToString("N")[..8]);
        var stdoutPath = Path.Combine(directory, "stdout.log");
        var stderrPath = Path.Combine(directory, "stderr.log");
        Xunit.Assert.True(stdoutPath.Length < 100, $"Capture path is too long under {tempRoot}");
        Xunit.Assert.True(stderrPath.Length < 100, $"Capture path is too long under {tempRoot}");

        try
        {
            Directory.CreateDirectory(directory);
            using var launch = OwnedProcessGroup.StartSuspendedWithFileCapture(
                new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/d /c echo short-capture",
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                stdoutPath,
                stderrPath);
            launch.Resume();
            Xunit.Assert.True(launch.WaitForOwnedExit(TimeSpan.FromSeconds(30)));
            Xunit.Assert.Equal("short-capture\r\n", File.ReadAllText(stdoutPath));
            Xunit.Assert.Equal(0L, new FileInfo(stderrPath).Length);
            Xunit.Assert.Equal(
                new[] { "stderr.log", "stdout.log" },
                Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToArray());
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
