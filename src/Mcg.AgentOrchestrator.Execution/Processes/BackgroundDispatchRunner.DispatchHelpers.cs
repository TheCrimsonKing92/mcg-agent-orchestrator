using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    // The detached dispatch host is the App's __dispatch-run subcommand. The App assembly sits next
    // to this Infrastructure assembly in every run context, so resolve it from the base directory.
    private static string ResolveDispatchHostAssembly() =>
        Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");

    private static bool IsLocalDispatch(TaskDispatchRecord dispatch) =>
        dispatch.WorkerName.Equals("local", StringComparison.OrdinalIgnoreCase);
}
