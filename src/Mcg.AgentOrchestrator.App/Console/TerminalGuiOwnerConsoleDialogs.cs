using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class TerminalGuiOwnerConsoleDialogs(IApplication app, CancellationToken token) : IOwnerConsoleDialogs
{
    private readonly int _uiThread = Environment.CurrentManagedThreadId;

    public Task<bool> ConfirmAsync(string title, string text) =>
        OnUiAsync(() => MessageBox.Query(app, title, text, "Accept", "Cancel") == 0);

    public Task ShowTextAsync(string title, string text) => OnUiAsync(() =>
    {
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(85), Height = Dim.Percent(80) };
        var content = new TextView { Text = text, ReadOnly = true, WordWrap = true, Width = Dim.Fill(), Height = Dim.Fill(1) };
        var close = new Button { Text = "Close", IsDefault = true };
        close.Accepting += (_, args) => { args.Handled = true; app.RequestStop(dialog); };
        dialog.Add(content);
        dialog.AddButton(close);
        app.Run(dialog);
        return true;
    });

    public Task<string?> PromptTextAsync(string title, string text) => OnUiAsync(() =>
    {
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(85), Height = Dim.Percent(65) };
        var explanation = new TextView { Text = text, ReadOnly = true, WordWrap = true, Width = Dim.Fill(), Height = Dim.Fill(4) };
        var input = new TextField { Y = Pos.Bottom(explanation), Width = Dim.Fill(), Height = 1 };
        string? result = null;
        var submit = new Button { Text = "Submit", IsDefault = true };
        var cancel = new Button { Text = "Cancel" };
        submit.Accepting += (_, args) => { args.Handled = true; result = input.Text; app.RequestStop(dialog); };
        cancel.Accepting += (_, args) => { args.Handled = true; app.RequestStop(dialog); };
        dialog.Add(explanation, input);
        dialog.AddButton(submit);
        dialog.AddButton(cancel);
        dialog.Initialized += (_, _) => input.SetFocus();
        app.Run(dialog);
        return result;
    });

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
