using System.Windows;
using LibVLCSharp.Shared;
using Surfio.Services;

namespace Surfio;

public partial class App : Application
{
    SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new SingleInstance();
        if (!_instance.IsFirst && SingleInstance.SendToFirst(e.Args.Length > 0 ? e.Args : ["--activate"]))
        {
            Shutdown();
            return;
        }

        Core.Initialize();

        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();

        _instance.Listen(args => Dispatcher.BeginInvoke(() => window.OpenFromOtherInstance(args)));

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "Surfio", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
