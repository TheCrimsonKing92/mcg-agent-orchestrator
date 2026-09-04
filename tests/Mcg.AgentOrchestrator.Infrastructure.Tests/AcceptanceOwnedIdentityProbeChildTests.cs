using System.Diagnostics;
using System.Text.Json;

public sealed class AcceptanceOwnedIdentityProbeChildTests
{
    [Xunit.Fact]
    public async Task PublishOwnedIdentityAndWaitForRelease()
    {
        var repositoryRoot = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return;
        }

        var configPath = Path.Combine(repositoryRoot, AcceptanceOwnedIdentityProbe.ConfigFileName);
        if (!File.Exists(configPath))
        {
            return;
        }

        var config = JsonSerializer.Deserialize<AcceptanceOwnedIdentityProbe>(File.ReadAllText(configPath));
        Xunit.Assert.NotNull(config);
        using var process = Process.GetCurrentProcess();
        File.WriteAllText(
            config.ReceiptPath,
            JsonSerializer.Serialize(new AcceptanceOwnedIdentityReceipt(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime()))));
        using var ready = EventWaitHandle.OpenExisting(config.ReadyEventName);
        using var release = EventWaitHandle.OpenExisting(config.ReleaseEventName);
        ready.Set();
        await AssemblyTempRedirectTests.WaitForSignalAsync(
            release,
            TestContext.Current.CancellationToken);
    }
}
