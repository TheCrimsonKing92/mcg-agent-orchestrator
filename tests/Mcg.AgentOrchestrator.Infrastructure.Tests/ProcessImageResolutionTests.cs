using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: constructed identities, injected reads/delays, no process mutation.
public sealed class ProcessImageResolutionTests
{
    private const string Loader = @"C:\WINDOWS\SYSTEM32\ntdll.dll";
    private const string Executable = @"C:\Program Files\dotnet\dotnet.exe";

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(Loader, false)]
    [InlineData(@"c:\windows\system32\NTDLL.DLL", false)]
    [InlineData("ntdll.dll", false)]
    [InlineData("  ntdll.dll  ", false)]
    [InlineData("C:/WINDOWS/SYSTEM32/ntdll.dll", false)]
    [InlineData(Executable, true)]
    [InlineData("kernel32.dll", true)]
    public void IsResolvedRecognizesOnlyMissingOrLoaderImages(string? image, bool expected) =>
        Assert.Equal(expected, ProcessImageResolution.IsResolved(image));

    [Theory]
    [InlineData(Loader, Executable, 0, 0, true)]
    [InlineData(Executable, Loader, 0, 0, true)]
    [InlineData(Loader, Loader, 0, 0, true)]
    [InlineData("", Executable, 0, 0, true)]
    [InlineData(Executable, Executable, 0, 0, true)]
    [InlineData("DOTNET.EXE", "dotnet.exe", 0, 0, true)]
    [InlineData(Executable, Executable, 0, 1, false)]
    [InlineData(Executable, Executable, 1, 0, false)]
    [InlineData(Loader, Executable, 0, 1, false)]
    [InlineData(Loader, Executable, 1, 0, false)]
    [InlineData(Executable, "other.exe", 0, 0, false)]
    public void SameProcessRequiresPidAndStartBeforeComparingResolvedImages(
        string recordedImage, string observedImage, int pidDelta, int startShiftDays, bool expected)
    {
        var recorded = new SpawnProcessIdentity(222, DateTimeOffset.UnixEpoch, recordedImage);
        var observed = new SpawnProcessIdentity(222 + pidDelta,
            DateTimeOffset.UnixEpoch.AddDays(startShiftDays), observedImage);

        Assert.Equal(expected, ProcessImageResolution.SameProcess(recorded, observed));
    }

    [Theory]
    [InlineData(Loader, Executable, Executable)]
    [InlineData(Executable, Loader, Executable)]
    [InlineData(null, Executable, Executable)]
    [InlineData(Executable, null, Executable)]
    public void PreferResolvedUpgradesAndNeverDowngradesImages(string? recorded, string? observed, string expected) =>
        Assert.Equal(expected, ProcessImageResolution.PreferResolved(recorded, observed));

    [Fact]
    public void RegistrationRetriesLoaderImagesUntilResolved()
    {
        using var process = Process.GetCurrentProcess();
        var attempts = 0;
        var delays = new List<int>();
        var resolved = new SpawnProcessIdentity(process.Id, DateTimeOffset.UnixEpoch, Executable);
        var result = SpawnProcessIdentityReader.ReadForRegistration(process,
            _ => ++attempts < 3 ? resolved with { ImagePath = Loader } : resolved,
            delays.Add, maxAttempts: 5, delayMilliseconds: 17);

        Assert.True(result.Succeeded);
        Assert.Equal(resolved, result.Identity);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, attempts);
        Assert.Equal([17, 17], delays);
        Assert.Equal("status=read attempts=3", result.Evidence);
    }

    [Fact]
    public void RegistrationExhaustionDoesNotReturnLoaderIdentity()
    {
        using var process = Process.GetCurrentProcess();
        var attempts = 0;
        var delays = new List<int>();
        var loader = new SpawnProcessIdentity(process.Id, DateTimeOffset.UnixEpoch, Loader);
        var result = SpawnProcessIdentityReader.ReadForRegistration(process,
            _ => { attempts++; return loader; },
            delays.Add, maxAttempts: 4, delayMilliseconds: 23);

        Assert.False(result.Succeeded);
        Assert.Null(result.Identity);
        Assert.Equal(4, result.Attempts);
        Assert.Equal(4, attempts);
        Assert.Equal([23, 23, 23], delays);
        Assert.Equal("status=unavailable attempts=4 reason=identity-unavailable", result.Evidence);
    }
}
