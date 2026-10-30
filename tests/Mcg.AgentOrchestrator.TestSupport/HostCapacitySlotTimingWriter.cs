using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Xunit;

/// <summary>Best-effort telemetry for completed host-capacity slot uses.</summary>
public static class HostCapacitySlotTimingWriter
{
    public const string VariableName = "MCG_TEST_SLOT_TIMING_FILE";
    private static readonly object AppendLock = new();

    public static string CurrentTestName()
    {
        try
        {
            var context = TestContext.Current;
            return context.TestClass?.TestClassName is { } className && context.TestMethod?.MethodName is { } methodName
                ? className + "." + methodName : "<unknown>";
        }
        catch (Exception) { return "<unknown>"; }
    }

    public static void TryAppend(string destination, string? testName, double waitMs, double heldMs, int slotCount)
    {
        try
        {
            var missingIdentity = string.IsNullOrWhiteSpace(testName) || testName == "<unknown>";
            if (missingIdentity) Diagnose(destination, "current test identity unavailable; recording <unknown>");
            var line = JsonSerializer.Serialize(new
            {
                test_name = missingIdentity ? "<unknown>" : testName,
                wait_ms = waitMs, held_ms = heldMs, slot_count = slotCount, process_id = Environment.ProcessId
            });
            // SharedJsonlFile also excludes concurrent writers across processes for the whole append.
            lock (AppendLock) SharedJsonlFile.AppendLine(destination, line);
        }
        catch (Exception error) { Diagnose(destination, error.GetType().Name + ": " + error.Message); }
    }

    private static void Diagnose(string destination, string cause)
    {
        try { Console.Error.WriteLine($"Host-capacity slot timing '{destination}': {cause}"); }
        catch (Exception) { /* Telemetry must never change a test verdict, including stderr failures. */ }
    }
}
