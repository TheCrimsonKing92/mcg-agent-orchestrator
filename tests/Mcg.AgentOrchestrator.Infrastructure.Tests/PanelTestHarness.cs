using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class PanelTestHarness : IDisposable
{
    internal sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
    internal PanelTestHarness()
    {
        Directory.CreateDirectory(Root);
        Store = new(Path.Combine(Root, "panel.db"));
        Store.Start(Time.UtcNow, []);
        Time.UtcNow = Time.UtcNow.AddSeconds(1);
        Kernel = new(Time);
        Goal = Kernel.CreateGoal("Panel test", [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
        Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
        Store.Start(Time.UtcNow, [new(Goal.Id.Value, Time.UtcNow)]);
        Host = NewHost();
    }
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-panel-test-" + Guid.NewGuid().ToString("N"));
    internal string ConductPath => Path.Combine(Root, "conduct-events.log");
    internal Clock Time { get; } = new();
    internal AgentOrchestratorKernel Kernel { get; }
    internal Goal Goal { get; }
    internal ConductorJudgePanelCaseStore Store { get; }
    internal PanelFakeJudge Sol { get; } = new("sol");
    internal PanelFakeJudge Sonnet { get; } = new("sonnet");
    internal ConductorJudgePanelHost Host { get; }
    internal string Candidate { get; set; } = new('a', 40);
    internal ConductorJudgePanelHost NewHost() => new(Store, Sol, Sonnet, _ => Candidate,
        new ConductEventLogWriter(ConductPath, utcNow: () => Time.UtcNow), () => Time.UtcNow);
    internal PanelCase Enqueue(string trigger) => Store.Enqueue(new(Goal.Id.Value, Candidate, new('b', 40),
        EffectiveAcceptanceCriteriaVersion.ComputeForGoal(Goal), trigger, "test-trigger", "decision-time packet"));
    internal async Task Complete(ConductorJudgePanelHost? host = null)
    {
        host ??= Host;
        host.ServiceTick(Kernel);
        await Signal(host.CurrentCase!, "panel case finished");
        host.ServiceTick(Kernel);
    }
    internal static async Task Signal(Task task, string expected)
    {
        // The bound diagnoses a hung signal only; no timing value decides the behavior assertion.
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new InvalidOperationException("Signal did not occur: " + expected); }
    }
    internal static string Answer(string id) => JsonSerializer.Serialize(new
    {
        schema = "panel-v0", case_id = id, assessment = "supported",
        next_action = new { kind = "no-action", owner = "operator", detail = "Evidence supports the decision" },
        discriminating_observation = "The named receipt", missing_evidence = Array.Empty<string>()
    });
    public void Dispose()
    {
        Host.Stop();
        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class PanelFakeJudge(string judge) : IConductorPanelJudgeRunner
{
    private int _calls;
    private TaskCompletionSource<bool>? _release;
    internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Calls => Volatile.Read(ref _calls);
    internal Func<PanelCase, string> Reply { get; set; } = item => PanelTestHarness.Answer(item.Id);
    internal PanelJudgeOutcome? Fault { get; set; }
    public string Judge => judge;
    public string? BindingError { get; set; }
    internal void Hold() => _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Release() => _release!.TrySetResult(true);
    public async Task<PanelJudgeResult> RunAsync(PanelCase item, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _calls);
        Started.TrySetResult(true);
        if (count == 2) SecondStarted.TrySetResult(true);
        if (_release is not null) await _release.Task.WaitAsync(cancellationToken);
        var answer = Fault switch
        {
            PanelJudgeOutcome.EmptyOutput => "",
            PanelJudgeOutcome.InvalidOutput => "```json\n" + Reply(item) + "\n```",
            _ => Reply(item)
        };
        return new(Judge, Fault ?? PanelV0Contract.Validate(answer, item.Id),
            Fault == PanelJudgeOutcome.InvocationFailed ? 1 : 0, answer, "");
    }
}
