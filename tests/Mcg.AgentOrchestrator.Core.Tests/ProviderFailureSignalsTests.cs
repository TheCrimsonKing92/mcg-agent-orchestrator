using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: fixed in-memory records; no files, shared state or processes.
public sealed class ProviderFailureSignalsTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConnectivityFailure_DisconnectedStream_PreservesBaseline()
    {
        // Baseline fb2e83943: DispatchFailureClassifier.cs:1942-1952,2059-2083,2181-2194.
        const string error = "ERROR: stream disconnected before completion";
        var verification = FailedRecord(standardError: error);

        Assert.True(ProviderFailureSignals.TryGetProviderConnectivityLine(verification, out var line));
        Assert.Equal(error, line);
        Assert.True(ProviderFailureSignals.IsRecoverableProviderConnectivityFailure(verification));
    }

    [Fact]
    public void AuthenticationFailure_NotSignedIn_PreservesBaseline()
    {
        // Baseline fb2e83943: DispatchFailureClassifier.cs:1931-1940,2085-2101,2166-2179.
        const string error = "ERROR: Not signed in";
        var verification = FailedRecord(standardError: error);

        Assert.True(ProviderFailureSignals.TryGetProviderAuthenticationLine(verification, out var line));
        Assert.Equal(error, line);
        Assert.True(ProviderFailureSignals.IsRecoverableProviderAuthenticationFailure(verification));
    }

    [Fact]
    public void ModelRejection_UnsupportedModel_PreservesBaseline()
    {
        // Baseline fb2e83943: DispatchFailureClassifier.cs:1954-1971,2103-2114,2196-2226.
        const string error = "ERROR: model example-model is not supported";
        var verification = FailedRecord(standardError: error);

        Assert.True(ProviderFailureSignals.TryGetProviderModelRejectionLine(verification, out var line));
        Assert.Equal(error, line);
        Assert.True(ProviderFailureSignals.IsRecoverableProviderModelRejectionFailure(verification));
    }

    [Fact]
    public void Interruption_FailedTurnWithoutCompletion_PreservesBaseline()
    {
        // Baseline fb2e83943: DispatchFailureClassifier.cs:1811-1883,1902-1929.
        var verification = FailedRecord(standardOutput: "{\"type\":\"turn.failed\"}");

        Assert.True(ProviderFailureSignals.IsProviderInterruptionFailure(verification));
    }

    [Fact]
    public void FailureSignals_UnrelatedVerification_PreservesBaseline()
    {
        // Baseline fb2e83943: DispatchFailureClassifier.cs:1738-1750,1811-1971,2166-2226.
        var verification = FailedRecord(standardOutput: "Validation failed: missing required field.");

        Assert.False(ProviderFailureSignals.IsRecoverableProviderConnectivityFailure(verification));
        Assert.False(ProviderFailureSignals.IsRecoverableProviderAuthenticationFailure(verification));
        Assert.False(ProviderFailureSignals.IsRecoverableProviderModelRejectionFailure(verification));
        Assert.False(ProviderFailureSignals.IsProviderInterruptionFailure(verification));
        Assert.False(ProviderFailureSignals.IsProviderNeutralProgressStallFailure(verification));
        Assert.False(ProviderFailureSignals.TryGetProviderConnectivityLine(verification, out var connectivity));
        Assert.Equal(string.Empty, connectivity);
        Assert.False(ProviderFailureSignals.TryGetProviderAuthenticationLine(verification, out var authentication));
        Assert.Equal(string.Empty, authentication);
        Assert.False(ProviderFailureSignals.TryGetProviderModelRejectionLine(verification, out var modelRejection));
        Assert.Equal(string.Empty, modelRejection);
    }

    private static TaskVerificationRecord FailedRecord(
        string standardOutput = "",
        string standardError = "") =>
        new("opaque command", "C:\\repo", 1, standardOutput, standardError, CompletedAt);
}
