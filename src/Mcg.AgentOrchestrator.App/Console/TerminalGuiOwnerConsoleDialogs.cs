using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class TerminalGuiOwnerConsoleDialogs(IApplication app, CancellationToken token) : IOwnerConsoleDialogs
{
    private readonly int _uiThread = Environment.CurrentManagedThreadId;

    public Task<bool> ConfirmAsync(string title, string text) => OnUiAsync(() =>
    {
        using var dialog = new OwnerConsoleTextDialog(title, text);
        var accepted = false;
        var accept = new Button { Text = "Accept", IsDefault = true };
        var cancel = new Button { Text = "Cancel" };
        accept.Accepting += (_, args) => { args.Handled = true; accepted = true; app.RequestStop(dialog); };
        cancel.Accepting += (_, args) => { args.Handled = true; app.RequestStop(dialog); };
        dialog.AddButton(accept); dialog.AddButton(cancel);
        Run(dialog);
        return accepted;
    });

    public async Task ShowTextAsync(string title, string text) => await ShowPageAsync(title, text);

    public async Task ShowDecisionAsync(OwnerConsoleDecisionDetail detail) => await OnUiAsync(() =>
    {
        using var dialog = new OwnerConsoleDecisionDialog(detail, action => app.Invoke(action));
        dialog.Closed += () => app.RequestStop(dialog);
        void Handle(object? sender, Key key)
        { if (app.TopRunnableView == dialog) _ = dialog.HandleKeyAsync(key); }
        var keyboard = app.Keyboard;
        if (keyboard is not null) keyboard.KeyDown += Handle;
        try { app.Run(dialog); }
        finally { if (keyboard is not null) keyboard.KeyDown -= Handle; }
        return true;
    });

    public Task<int?> ShowPageAsync(string title, string text, IReadOnlyList<int>? choiceLines = null) => OnUiAsync(() =>
    {
        using var dialog = new OwnerConsoleTextDialog(title, text, choiceLines);
        int? selected = null;
        dialog.ChoiceAccepted += choice => { selected = choice; app.RequestStop(dialog); };
        var close = new Button { Text = "Close", IsDefault = true };
        close.Accepting += (_, args) => { args.Handled = true; app.RequestStop(dialog); };
        dialog.AddButton(close);
        Run(dialog);
        return selected;
    });

    public Task<string?> PromptTextAsync(string title, string text) => OnUiAsync(() =>
    {
        using var dialog = new OwnerConsoleTextDialog(title, text);
        dialog.Body.Height = Dim.Fill(4);
        var input = new TextField { Y = Pos.Bottom(dialog.Body), Width = Dim.Fill(), Height = 1 };
        string? result = null;
        var submit = new Button { Text = "Submit", IsDefault = true };
        var cancel = new Button { Text = "Cancel" };
        submit.Accepting += (_, args) => { args.Handled = true; result = input.Text; app.RequestStop(dialog); };
        cancel.Accepting += (_, args) => { args.Handled = true; app.RequestStop(dialog); };
        dialog.Add(input);
        dialog.AddButton(submit);
        dialog.AddButton(cancel);
        dialog.Initialized += (_, _) => input.SetFocus();
        Run(dialog);
        return result;
    });

    private void Run(OwnerConsoleTextDialog dialog)
    {
        void Handle(object? sender, Key key)
        { if (app.TopRunnableView == dialog && dialog.Body.HasFocus) dialog.HandleKey(key); }
        var keyboard = app.Keyboard;
        if (keyboard is not null) keyboard.KeyDown += Handle;
        try { app.Run(dialog); }
        finally { if (keyboard is not null) keyboard.KeyDown -= Handle; }
    }

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        token.ThrowIfCancellationRequested();
        if (Environment.CurrentManagedThreadId == _uiThread) return Task.FromResult(action());
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = token.Register(() => completion.TrySetCanceled(token));
        app.Invoke(() =>
        {
            try { if (!token.IsCancellationRequested) completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { cancellation.Dispose(); }
        });
        return completion.Task;
    }
}
