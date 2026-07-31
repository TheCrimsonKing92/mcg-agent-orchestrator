using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

// `dotnet build-server shutdown` tears down VBCSCompiler and the MSBuild node processes for the ENTIRE logon
// session, not just this test process. When a test reaches it, the sibling acceptance lanes and the operator's
// own conduct-loop gate builds take CS2012 or a compiler crash mid-compile - so THE VICTIM'S VERDICT FLIPS,
// NOT THE PERPETRATOR'S. No lane scheduling can fix that, which is why it has to be seamed off here.
//
// `DotnetBuildEnvironmentManager.ShutdownBuildServersForTests` is null by default, and null means "run the
// real thing", so every lease disposal in a test that forgets to stub it fires a session-wide shutdown.
// Invert that default for the whole assembly: tests get the no-op unless they deliberately opt out.
//
// Tests that assert shutdown behaviour still substitute their own delegate; they must restore `SafeDefault`
// rather than null, or they hand the dangerous default back to every test that runs after them.
internal static class AssemblyBuildServerShutdownIsolation
{
    // Assigned (not just returned) so the seam is non-null from assembly load onward.
    internal static readonly Action SafeDefault = static () => { };

    [ModuleInitializer]
    internal static void Install()
    {
        DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = SafeDefault;
    }
}
