using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace Qalam.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        try { _execute(parameter); }
        catch (Exception ex) { UiHelpers.ShowError(ex); }
    }
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _busy;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }

    public AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_busy && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try { await _execute(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UiHelpers.ShowError(ex); }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public static class UiHelpers
{
    public static void ShowError(Exception ex)
    {
        var message = ex switch
        {
            Qalam.Core.AI.AiException ai => ai.UserMessage,
            Qalam.Core.Workflow.ContextMissingException cm =>
                cm.Message + "\n\nOpen Brand Guidelines to fill them in (or switch off 'Strict context' in Settings).",
            _ => ex.Message
        };
        MessageBox.Show(message, "Qalam", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static bool Confirm(string message) =>
        MessageBox.Show(message, "Qalam", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) action(); else d.BeginInvoke(action);
    }

    public static void CopyToClipboard(string? text)
    {
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static string Local(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy, HH:mm");
}

/// <summary>A dashboard section.</summary>
public abstract class SectionViewModel : ObservableObject
{
    public abstract string Title { get; }
    public abstract string Icon { get; }
    public virtual void Refresh() { }
    public virtual void OnActivated() => Refresh();
}
