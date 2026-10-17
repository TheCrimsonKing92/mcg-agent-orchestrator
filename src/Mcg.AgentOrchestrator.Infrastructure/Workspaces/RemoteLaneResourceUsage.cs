using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneResourceUsage(double TestHostCpuSeconds, double TestHostPeakWorkingSetBytes,
    double AvailableMemoryMbMin, int ResourceSamples)
{
    internal static RemoteLaneResourceUsage? TryRead(JsonElement? status)
    {
        if (status is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("resourceSamples", out var samples) || samples.ValueKind != JsonValueKind.Number ||
            !samples.TryGetInt32(out var count) || count < 1 ||
            Read(value, "testHostCpuSeconds") is not { } cpu ||
            Read(value, "testHostPeakWorkingSetBytes") is not { } peak ||
            Read(value, "availableMemoryMbMin") is not { } available) return null;
        return new(cpu, peak, available, count);
    }

    private static double? Read(JsonElement status, string name) =>
        status.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
        field.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0 ? value : null;
}
