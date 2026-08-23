using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

public sealed class FocusedProcessFixtureTests
{
    [Xunit.Fact]
    public void WaitsOnTheConfiguredReleaseEvent()
    {
        var markerPath = Environment.GetEnvironmentVariable("FOCUSED_STARTED_MARKER");
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            return;
        }

        var releasePath = Environment.GetEnvironmentVariable("FOCUSED_RELEASE");
        Assert.False(string.IsNullOrWhiteSpace(releasePath));
        File.WriteAllText(markerPath, "started");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(releasePath), TimeSpan.FromMinutes(1)));
    }
}
