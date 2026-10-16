using Mcg.AgentOrchestrator.Infrastructure;

return WorkerResultSubmission.RunSubmitCommand(args, Console.Out, Console.Error, () => DateTimeOffset.UtcNow);
