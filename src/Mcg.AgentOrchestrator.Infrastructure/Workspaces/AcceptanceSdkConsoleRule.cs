namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceSdkConsoleRule
{
    internal static bool RequiresOwnConsole(string[] arguments)
    {
        if (arguments is null || arguments.Length == 0)
            return false;

        var executable = arguments[0].Replace('\\', '/');
        if (!Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return false;

        var command = arguments.Skip(1).FirstOrDefault(argument => !argument.StartsWith('-'));
        return command is null ||
            (!command.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
             !command.Equals("exec", StringComparison.OrdinalIgnoreCase));
    }
}
