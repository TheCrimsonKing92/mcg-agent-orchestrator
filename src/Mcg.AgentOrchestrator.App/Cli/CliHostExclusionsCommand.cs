using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliHostExclusionsCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("host-exclusions", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args)
    {
        try
        {
            ParseApply(args);
            var inputs = HostScanExclusionInputs.CaptureCurrent(OrchestratorWorkspace.ResolveRepoRoot());
            Console.WriteLine($"Host roots: user-profile={inputs.UserProfile} temp={inputs.TempPath}");
            var adapter = new DefenderPreferenceCmdletAdapter();
            return Run(args, HostScanExclusionRoots.Compute(inputs), adapter, adapter, Console.Out, Console.Error);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    internal static int Run(IReadOnlyList<string> args, IReadOnlyList<string> roots,
        IHostExclusionReader reader, IHostExclusionWriter writer, TextWriter output, TextWriter error)
    {
        try
        {
            var apply = ParseApply(args);
            if (apply && !reader.IsElevated)
            {
                error.WriteLine("Run host-exclusions --apply in an elevated PowerShell to add exclusions.");
                return 1;
            }
            var snapshot = reader.Read();
            if (!snapshot.CanRead)
            {
                output.WriteLine("The Defender exclusion list cannot be read without elevation.");
                foreach (var root in roots) output.WriteLine($"required {root}");
                output.WriteLine("Run this command in an elevated PowerShell:");
                output.WriteLine("Add-MpPreference -ExclusionPath " +
                    string.Join(",", roots.Select(root => "'" + root.Replace("'", "''") + "'")));
                return apply ? 1 : 0;
            }

            var present = Normalize(snapshot.Paths);
            if (!apply)
            {
                PrintState(roots, present, output);
                return 0;
            }

            var failed = false;
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (present.Contains(HostScanExclusionRoots.NormalizePath(root))) continue;
                try
                {
                    writer.AddExclusionPath(root);
                    output.WriteLine($"added {root}");
                }
                catch (Exception ex)
                {
                    failed = true;
                    error.WriteLine($"failed {root}: {ex.Message}");
                }
            }
            var final = reader.Read();
            if (!final.CanRead) throw new InvalidOperationException("Cannot re-read Defender exclusions; run elevated.");
            present = Normalize(final.Paths);
            output.WriteLine("Final state:");
            PrintState(roots, present, output);
            if (roots.Any(root => !present.Contains(HostScanExclusionRoots.NormalizePath(root))))
            {
                error.WriteLine("Required exclusions remain missing; check Defender policy or tamper protection.");
                return 1;
            }
            return failed ? 1 : 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static HashSet<string> Normalize(IEnumerable<string> paths) =>
        paths.Select(HostScanExclusionRoots.NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void PrintState(IEnumerable<string> roots, HashSet<string> present, TextWriter output)
    {
        foreach (var root in roots)
            output.WriteLine($"{(present.Contains(HostScanExclusionRoots.NormalizePath(root)) ? "present" : "missing")} {root}");
    }

    private static bool ParseApply(IReadOnlyList<string> args)
    {
        CliCommandHelp.ThrowIfInvalidFlags(args);
        if (!IsCommand(args) || args.Skip(1).Any(arg => !arg.Equals("--apply", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(CliCommandHelp.HostExclusionsUsage);
        return args.Count > 1;
    }
}
