using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Moondrop.Wpf;

public enum ShellLayoutMode
{
    Wide,
    Medium,
    Narrow
}

public static class ResponsiveShell
{
    public static ShellLayoutMode Classify(double width) =>
        width < 900
            ? ShellLayoutMode.Narrow
            : width < 1240
                ? ShellLayoutMode.Medium
                : ShellLayoutMode.Wide;
}

internal sealed class AsyncCloseCoordinator(
    Func<ValueTask> disposeAsync,
    Action requestFinalClose,
    Action<Exception> reportFailure)
{
    private const int Open = 0;
    private const int Disposing = 1;
    private const int ReadyForFinalClose = 2;
    private const int FinalClose = 3;
    private int _state;

    public bool ShouldDeferClose()
    {
        var state = Volatile.Read(ref _state);
        if (state == ReadyForFinalClose)
        {
            Interlocked.CompareExchange(ref _state, FinalClose, ReadyForFinalClose);
            return false;
        }
        if (state == FinalClose)
            return false;
        if (state == Open && Interlocked.CompareExchange(ref _state, Disposing, Open) == Open)
            _ = DisposeThenCloseAsync();
        return true;
    }

    private async Task DisposeThenCloseAsync()
    {
        try
        {
            await disposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                reportFailure(ex);
            }
            catch
            {
                // Shutdown must still reach the final close if error reporting fails.
            }
        }
        finally
        {
            Volatile.Write(ref _state, ReadyForFinalClose);
            requestFinalClose();
        }
    }
}

public sealed class AccessibleDialog : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new AccessibleDialogAutomationPeer(this);
}

internal sealed class AccessibleDialogAutomationPeer(AccessibleDialog owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => "Dialog";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Window;

    protected override string GetNameCore() =>
        AutomationProperties.GetName(Owner) is { Length: > 0 } name ? name : base.GetNameCore();

    protected override string GetHelpTextCore() =>
        AutomationProperties.GetHelpText(Owner) is { Length: > 0 } help ? help : base.GetHelpTextCore();
}

public sealed class DialogState : NotifyObject
{
    private TaskCompletionSource<bool>? _decision;
    private bool _isOpen;
    private string _title = "";
    private string _message = "";
    private string _primaryText = "Continue";

    public DialogState()
    {
        ConfirmCommand = new RelayCommand(() =>
        {
            Confirm();
            return Task.CompletedTask;
        });
        CancelCommand = new RelayCommand(() =>
        {
            Cancel();
            return Task.CompletedTask;
        });
    }

    public bool IsOpen { get => _isOpen; private set => SetField(ref _isOpen, value); }
    public string Title { get => _title; private set => SetField(ref _title, value); }
    public string Message { get => _message; private set => SetField(ref _message, value); }
    public string PrimaryText { get => _primaryText; private set => SetField(ref _primaryText, value); }
    public System.Windows.Input.ICommand ConfirmCommand { get; }
    public System.Windows.Input.ICommand CancelCommand { get; }

    public Task<bool> AskAsync(string title, string message, string primaryText)
    {
        _decision?.TrySetResult(false);
        Title = title;
        Message = message;
        PrimaryText = primaryText;
        IsOpen = true;
        _decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _decision.Task;
    }

    public void Confirm() => Complete(true);

    public void Cancel() => Complete(false);

    private void Complete(bool result)
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        var decision = _decision;
        _decision = null;
        decision?.TrySetResult(result);
    }
}

public sealed class StatusBannerState : NotifyObject
{
    private bool _isVisible;
    private string _title = "";
    private string _message = "";

    public StatusBannerState()
    {
        DismissCommand = new RelayCommand(() =>
        {
            Dismiss();
            return Task.CompletedTask;
        });
    }

    public bool IsVisible { get => _isVisible; private set => SetField(ref _isVisible, value); }
    public string Title { get => _title; private set => SetField(ref _title, value); }
    public string Message { get => _message; private set => SetField(ref _message, value); }
    public System.Windows.Input.ICommand DismissCommand { get; }

    public void ShowError(string title, string message)
    {
        Title = title;
        Message = message;
        IsVisible = true;
    }

    public void Dismiss() => IsVisible = false;
}

public sealed class ShellPageVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is ShellPage page
        && Enum.TryParse<ShellPage>(parameter?.ToString(), true, out var expected)
        && page == expected
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
