using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class IcaclsIntegrityLabelerTestsCommand
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(7, false)]
    public void CommandExitAndBoundedDiagnosticsComeFromTheActualChild(int exitCode, bool succeeds)
    {
        var labeler = new IcaclsIntegrityLabeler(info =>
        {
            Assert.Equal("icacls", info.FileName);
            Assert.Equal(["/setintegritylevel", "(OI)(CI)L", "/T"], info.ArgumentList.Skip(1));
            info.FileName = WorkerShell.Executable;
            info.ArgumentList.Clear();
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                $"[Console]::Out.Write(('o' * 65536) + 'tail-marker'); [Console]::Error.Write('error-marker' + ('e' * 65536)); exit {exitCode}" })
                info.ArgumentList.Add(arg);
            return Process.Start(info);
        });

        var result = labeler.SetIntegrityWithDiagnostics(Path.GetTempPath(), "(OI)(CI)L", recursive: true);

        Assert.Equal(succeeds, result.Succeeded);
        using var json = JsonDocument.Parse(result.Diagnostic);
        var root = json.RootElement;
        Assert.Equal(exitCode, root.GetProperty("exitCode").GetInt32());
        Assert.True(root.GetProperty("completed").GetBoolean());
        Assert.False(root.GetProperty("timedOut").GetBoolean());
        Assert.True(root.GetProperty("drainsCompleted").GetBoolean());
        var output = root.GetProperty("stdoutTail").GetString()!;
        var error = root.GetProperty("stderrPrefix").GetString()!;
        Assert.Equal(4096, output.Length);
        Assert.Equal(4096, error.Length);
        Assert.EndsWith("tail-marker", output);
        Assert.StartsWith("error-marker", error);
    }

    [Fact]
    public void StartExceptionIsRetainedAndLegacyBooleanStillFailsClosed()
    {
        var labeler = new IcaclsIntegrityLabeler(_ => throw new Win32Exception(5, "sentinel-start-denied"));
        var result = labeler.SetIntegrityWithDiagnostics(Path.GetTempPath(), "L", recursive: false);
        Assert.False(result.Succeeded);
        Assert.False(labeler.SetIntegrity(Path.GetTempPath(), "L", recursive: false));
        using var json = JsonDocument.Parse(result.Diagnostic);
        Assert.Equal("Win32Exception", json.RootElement.GetProperty("exceptionType").GetString());
        Assert.Equal("sentinel-start-denied", json.RootElement.GetProperty("exceptionMessage").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("exitCode").ValueKind);
    }
}
