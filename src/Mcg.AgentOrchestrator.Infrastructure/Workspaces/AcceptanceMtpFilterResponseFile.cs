using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns lossless filter transport; planning never reads or writes the filesystem.
internal static class AcceptanceMtpFilterResponseFile
{
    internal const int InlineCommandBudget = 4096;

    internal static AcceptanceMtpFilterSpill Plan(IReadOnlyList<string> arguments)
    {
        var unchanged = new AcceptanceMtpFilterSpill(arguments as string[] ?? arguments.ToArray(), null, null);
        if (arguments.Count < 2 || !IsDotnetHost(arguments[0]) ||
            !arguments[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            arguments.Sum(argument => (long)argument.Length + 2) + arguments.Count - 1 <= InlineCommandBudget)
            return unchanged;

        string? directory = null;
        for (var i = 2; i + 1 < arguments.Count; i++)
            if (arguments[i] == "--results-directory")
            {
                directory = arguments[i + 1];
                break;
            }
        if (directory is null) return unchanged;

        var inline = new List<string>();
        var spilled = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i >= 2 && i + 1 < arguments.Count && IsFilterOption(arguments[i]))
            {
                spilled.Add(arguments[i]);
                spilled.Add(arguments[++i]);
            }
            else inline.Add(arguments[i]);
        }
        if (spilled.Count == 0) return unchanged;
        foreach (var entry in spilled)
            if (entry.Any(char.IsWhiteSpace) || entry.Contains('"') || entry.StartsWith('#') || entry.StartsWith('@'))
                throw new InvalidDataException($"MTP response-file entry '{entry}' cannot be represented losslessly.");

        var text = string.Join('\n', spilled);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        var path = directory.TrimEnd('/', '\\') + "/filter-" + hash + ".rsp";
        inline.Add("@" + path);
        return new AcceptanceMtpFilterSpill(inline.ToArray(), path, text);
    }

    internal static string[] Apply(string[] arguments)
    {
        var spill = Plan(arguments);
        if (spill.ResponseFilePath is not { } path) return arguments;
        var directory = arguments[Array.IndexOf(arguments, "--results-directory", 2) + 1];
        // The child may have a different working directory; never write relative artifacts here.
        if (!Path.IsPathRooted(directory)) return arguments;
        Directory.CreateDirectory(directory);
        var bytes = Encoding.UTF8.GetBytes(spill.ResponseFileText!);
        if (HasSameBytes(path, bytes)) return spill.Arguments;

        var temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            try
            {
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A concurrent writer of the same content may have won the move.
                if (!HasSameBytes(path, bytes)) throw;
            }
        }
        finally
        {
            File.Delete(temporary);
        }
        return spill.Arguments;
    }

    private static bool HasSameBytes(string path, byte[] bytes) =>
        File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);

    private static bool IsDotnetHost(string argument) =>
        Path.GetFileName(argument).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(argument).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsFilterOption(string argument) => argument is
        "--filter-class" or "--filter-not-class" or "--filter-method" or "--filter-not-method" or "--filter-not-trait";
}

internal sealed record AcceptanceMtpFilterSpill(string[] Arguments, string? ResponseFilePath, string? ResponseFileText);
