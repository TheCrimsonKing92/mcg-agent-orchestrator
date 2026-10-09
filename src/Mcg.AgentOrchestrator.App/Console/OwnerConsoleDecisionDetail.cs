namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// The controller owns this session; model refreshes push live rows, never dialog polling.
internal sealed class OwnerConsoleDecisionDetail : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeZoneInfo _zone;
    private readonly Func<OwnerConsoleDecision, bool, Action<string>, Task> _answer;
    private readonly Func<CancellationToken, Task<OwnerQuestionResolution?>> _readResolution;
    private readonly Func<string, Task> _help;
    private readonly CancellationTokenSource _stop;
    private OwnerConsoleDecision _decision;
    private bool _open = true;
    private bool _answering;
    private bool _disposed;
    private bool _reading;
    private int _revision;
    private OwnerQuestionResolution? _resolution;
    private string? _failure;
    private string _notice = "";
    private Task _refresh = Task.CompletedTask;

    internal event Action? Changed;
    internal string Id => _decision.Id;
    internal Task LastRefresh { get { lock (_gate) return _refresh; } }
    internal sealed record Presentation(string Text, string Banner, string Notice, bool CanAnswer, bool CanAcceptDefault);

    internal Presentation State
    {
        get
        {
            lock (_gate)
            {
                var text = $"{_decision.GoalId} | {_decision.Kind}\n{_decision.FullText}\nblast radius: {_decision.BlastRadius}\nconfidence: {_decision.Confidence}\ndefault: {_decision.ProposedDefault}";
                var banner = "";
                if (!_open)
                {
                    if (_resolution is { } resolved)
                    {
                        var at = TimeZoneInfo.ConvertTime(resolved.ResolvedAt, _zone).ToString("HH:mm:ss");
                        banner = resolved.Outcome == "Answered" ? $"Resolved: answered by {resolved.Answerer} at {at}" :
                            $"Resolved: {resolved.Outcome.ToLowerInvariant()} at {at}";
                        if (resolved.Answer is { } answer) text += "\nAnswer:\n" + answer;
                    }
                    else banner = _failure is not null ? "No longer open (resolution unavailable: " + _failure + ")" :
                        _reading ? "No longer open; reading how it was resolved…" : "No longer open";
                }
                return new(text, banner, _notice, _open, _open && !string.IsNullOrWhiteSpace(_decision.ProposedDefault));
            }
        }
    }

    internal OwnerConsoleDecisionDetail(OwnerConsoleDecision decision, TimeZoneInfo zone,
        Func<OwnerConsoleDecision, bool, Action<string>, Task> answer,
        Func<CancellationToken, Task<OwnerQuestionResolution?>> readResolution, Func<string, Task> help,
        CancellationToken token = default)
    {
        _decision = decision; _zone = zone; _answer = answer; _readResolution = readResolution; _help = help;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    internal void Observe(OwnerConsoleDecision? row)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (row is not null && (row.Id != _decision.Id || row.GoalId != _decision.GoalId))
                throw new InvalidOperationException("A decision detail cannot observe a different question.");
            if (row is not null)
            {
                _revision++;
                _decision = row; _open = true; _resolution = null; _failure = null;
            }
            else
            {
                _open = false;
                // A later tick can add the applied-answer receipt after the live row disappears.
                if (_refresh.IsCompleted)
                {
                    _reading = true;
                    var revision = _revision;
                    var token = _stop.Token;
                    _refresh = Task.Run(() => ReadResolutionAsync(revision, token));
                }
            }
        }
        Changed?.Invoke();
    }

    private async Task ReadResolutionAsync(int revision, CancellationToken token)
    {
        OwnerQuestionResolution? resolution = null;
        string? failure = null;
        try { resolution = await _readResolution(token).WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) { failure = ex.Message; }
        lock (_gate)
        {
            if (_disposed || _open || revision != _revision) return;
            _resolution = resolution; _failure = failure;
            _reading = false;
        }
        Changed?.Invoke();
    }

    internal async Task AnswerAsync(bool acceptDefault)
    {
        OwnerConsoleDecision decision;
        lock (_gate)
        {
            if (_disposed || !_open || _answering || acceptDefault && string.IsNullOrWhiteSpace(_decision.ProposedDefault)) return;
            _answering = true;
            decision = _decision;
        }
        try { await _answer(decision, acceptDefault, ShowNotice); }
        finally { lock (_gate) _answering = false; }
    }

    internal void ShowNotice(string text)
    {
        lock (_gate) { if (_disposed) return; _notice = text; }
        Changed?.Invoke();
    }

    internal Task HelpAsync()
    {
        var state = State;
        return _help(OwnerConsoleKeyHints.DecisionDetailHelpText(state.CanAnswer, state.CanAcceptDefault));
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _stop.Cancel(); _stop.Dispose();
    }
}
