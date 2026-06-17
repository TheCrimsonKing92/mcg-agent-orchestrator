// AsyncLocalConsoleRouter (ConsoleCapture.cs) installs once via [ModuleInitializer] and
// routes Console.Out writes through AsyncLocal<TextWriter?>, isolating captures per
// async context. Parallel collection execution is safe.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = false)]
