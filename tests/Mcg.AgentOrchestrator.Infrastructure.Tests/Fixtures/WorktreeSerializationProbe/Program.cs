using System.IO.Pipes;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// A small real-process subject: no test-host discovery, ambient switches or nested build.
try
{
    if (args.Length == 3 && args[0] == "create")
    {
        var path = GoalWorktrees.Ensure(args[1], new GoalId(args[2]));
        Console.WriteLine(JsonSerializer.Serialize(new { Path = path, ProcessId = Environment.ProcessId }));
        return 0;
    }

    if (args.Length == 1 && args[0] == "clean")
    {
        await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput());
        return 0;
    }

    if (args.Length == 2 && args[0] == "smudge")
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(guard.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("checkout-paused");
        if (await reader.ReadLineAsync(guard.Token) != "release")
            throw new InvalidOperationException("Checkout release was not received.");
        // A git smudge filter must preserve the blob bytes and reserve stdout for them.
        await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput(), guard.Token);
        return 0;
    }

    throw new ArgumentException("Expected create <repository> <goal-id>, smudge <pipe-name>, or clean.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 2;
}
