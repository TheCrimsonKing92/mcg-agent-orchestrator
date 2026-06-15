using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchProcessHostTests
{
    [Xunit.Fact(DisplayName = "DispatchProcessHost_parameters_round_trip_via_camelCase_json")]
    public void DispatchProcessHostParametersRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "dispatch.json");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                dir,
                Path.Combine(dir, "out.log"),
                Path.Combine(dir, "err.log"),
                Path.Combine(dir, "exit.txt"),
                Path.Combine(dir, "heartbeat.json"),
                ShutdownBuildServerOnExit: true,
                DisableSharedCompilation: true);

            DispatchProcessHost.WriteParameters(path, parameters);

            var json = File.ReadAllText(path);
            // The detached host reads this with a camelCase policy, so the keys must be camelCase.
            Assert.True(json.Contains("\"command\"", StringComparison.Ordinal));
            Assert.True(json.Contains("\"disableSharedCompilation\"", StringComparison.Ordinal));

            var roundTripped = JsonSerializer.Deserialize<DispatchProcessHost.DispatchRunParameters>(
                json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.Equal(parameters, roundTripped);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
