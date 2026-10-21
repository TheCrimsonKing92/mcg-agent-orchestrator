using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// The same bounded fixture can be compiled against old/new immutable application revisions.
// Reflection is confined to adapting Claim's old two-argument/new explicit-workspace signatures.
internal static class AcceptanceCustodyProbe
{
    internal static int Run(string root, string token)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root, root, null,
            new OrchestratorProjectRegistry(Path.Combine(root, "registry"), Path.Combine(root, "data")));
        var binding = new AcceptanceHolderBinding(token, Path.Combine(root, token + ".attempt.json"), "fixture", "slot-0");
        var lease = new AcceptanceHolderLease();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var type = typeof(AcceptanceHolderLease);
        var identity = type.GetMethod("CurrentGeneration", flags)!.Invoke(null, null)!;
        type.GetMethod("Reserve", flags)!.Invoke(lease, [binding, identity]);
        var claim = type.GetMethod("Claim", flags)!;
        object?[] arguments = claim.GetParameters().Length == 2 ? [binding, identity] : [binding, identity, workspace];
        var started = Stopwatch.GetTimestamp();
        using var containment = (IDisposable)claim.Invoke(lease, arguments)!;
        var admissionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var custody = containment.GetType().GetProperty("CustodianIdentity", flags)!.GetValue(containment);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            AdmissionMilliseconds = admissionMilliseconds, Host = Environment.ProcessPath,
            Application = typeof(OrchestratorWorkspace).Assembly.Location, Custodian = custody,
            Lease = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(binding.LeasePath)),
            binding.SettlementPath
        }));
        Console.Out.Flush();
        if (Console.ReadLine() != "exit") throw new InvalidOperationException("Fixture exit gate was not released.");
        return 0;
    }
}
