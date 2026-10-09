using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleDecisionDialog : Dialog
{
    private readonly OwnerConsoleDecisionDetail _detail;
    private readonly Action<Action> _invoke;
    private readonly Label _banner = new() { Width = Dim.Fill() };
    private readonly ListView _body = new() { Width = Dim.Fill() };
    private readonly Label _notice = new() { Width = Dim.Fill() };
    private readonly Label _hint = new() { Width = Dim.Fill() };
    private readonly Button _answer = new() { Text = "Answer (r)" };
    private readonly Button _accept = new() { Text = "Accept default (a)" };
    private readonly Button _close = new() { Text = "Close", IsDefault = true };
    private OwnerConsoleTextPage _page = new("", 60, 15);
    private string? _text;
    private bool _formatting;
    private bool _disposed;

    internal event Action? Closed;
    internal string BannerText { get; private set; } = "";
    internal string NoticeText { get; private set; } = "";
    internal string HintText { get; private set; } = "";
    internal IReadOnlyList<string> Lines => _page.Lines;
    internal IReadOnlyList<string> Actions => new[] { _answer, _accept, _close }
        .Where(button => button.Visible).Select(button => button.Text.ToString()).ToArray();
    internal Task LastRefresh => _detail.LastRefresh;

    internal OwnerConsoleDecisionDialog(OwnerConsoleDecisionDetail detail, Action<Action> invoke)
    {
        _detail = detail; _invoke = invoke;
        Title = "Decision"; Width = Dim.Percent(85); Height = Dim.Percent(80);
        Add(_banner, _body, _notice, _hint);
        AddButton(_answer); AddButton(_accept); AddButton(_close);
        _answer.Accepting += (_, args) => { args.Handled = true; _ = AnswerAsync(false); };
        _accept.Accepting += (_, args) => { args.Handled = true; _ = AnswerAsync(true); };
        _close.Accepting += (_, args) => { args.Handled = true; Closed?.Invoke(); };
        _body.ViewportChanged += (_, _) => Render();
        ViewportChanged += (_, _) => Render();
        Initialized += (_, _) => _body.SetFocus();
        _detail.Changed += Changed;
        Render();
    }

    private void Changed() => _invoke(Render);

    internal async Task HandleKeyAsync(Key key)
    {
        var state = _detail.State;
        var character = char.ToLowerInvariant((char)key.AsRune.Value);
        if (character == 'r' && state.CanAnswer || character == 'a' && state.CanAcceptDefault)
        { key.Handled = true; await AnswerAsync(character == 'a'); }
        else if (character == '?')
        {
            key.Handled = true;
            try { await _detail.HelpAsync(); }
            catch (Exception ex) { _detail.ShowNotice("error: " + ex.Message); }
        }
        else if (key == Key.Esc) { key.Handled = true; Closed?.Invoke(); }
        else if (_body.HasFocus && _page.HandleKey(key)) { key.Handled = true; Render(); }
    }

    private async Task AnswerAsync(bool acceptDefault)
    {
        try { await _detail.AnswerAsync(acceptDefault); }
        catch (Exception ex) { _detail.ShowNotice("error: " + ex.Message); }
    }

    private void Render()
    {
        if (_disposed || _formatting) return;
        _formatting = true;
        try
        {
            var state = _detail.State;
            BannerText = state.Banner; NoticeText = state.Notice;
            HintText = OwnerConsoleKeyHints.DecisionDetailHint(state.CanAnswer, state.CanAcceptDefault);
            var width = _body.Viewport.Width > 1 ? _body.Viewport.Width : 60;
            FormatLabel(_banner, BannerText, width);
            var noticeHeight = FormatLabel(_notice, NoticeText, width);
            var hintHeight = FormatLabel(_hint, HintText, width);
            _body.Y = Pos.Bottom(_banner);
            _hint.Y = Pos.AnchorEnd(hintHeight + 1);
            _notice.Y = Pos.AnchorEnd(hintHeight + noticeHeight + 1);
            _body.Height = Dim.Fill(hintHeight + noticeHeight + 1);
            var height = _body.Viewport.Height > 0 ? _body.Viewport.Height : 15;
            if (_text != state.Text) { _text = state.Text; _page = new(state.Text, width, height); }
            else _page.Resize(width, height);
            _body.SetSource(new ObservableCollection<string>(_page.Visible));
            _body.SelectedItem = _page.Selected - _page.Offset;
            if ((!state.CanAnswer && _answer.HasFocus) || (!state.CanAcceptDefault && _accept.HasFocus))
                _close.SetFocus();
            _answer.Visible = state.CanAnswer; _accept.Visible = state.CanAcceptDefault;
        }
        finally { _formatting = false; }
    }

    private static int FormatLabel(Label label, string text, int width)
    {
        var lines = new OwnerConsoleTextPage(text, width, 1).Lines;
        label.Text = string.Join("\n", lines);
        label.Height = text.Length == 0 ? 0 : lines.Count;
        return text.Length == 0 ? 0 : lines.Count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _disposed = true; _detail.Changed -= Changed; }
        base.Dispose(disposing);
    }
}
