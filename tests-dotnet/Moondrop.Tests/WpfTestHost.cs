using System.Windows;
using System.Windows.Threading;

namespace Moondrop.Tests;

internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> TestDispatcher = new(CreateDispatcher);

    public static T Run<T>(Func<T> action) => TestDispatcher.Value.Invoke(action);

    public static void Run(Action action) => TestDispatcher.Value.Invoke(action);

    public static Task RunAsync(Func<Task> action) =>
        TestDispatcher.Value.InvokeAsync(action).Task.Unwrap();

    private static Dispatcher CreateDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Moondrop.Wpf.App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            app.InitializeComponent();
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Moondrop WPF test dispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
