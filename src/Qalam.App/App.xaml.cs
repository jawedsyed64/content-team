using System.Windows;
using System.Windows.Threading;
using Qalam.App.Services;
using Qalam.App.ViewModels;

namespace Qalam.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            var services = new AppServices();
            var window = new MainWindow { DataContext = new MainViewModel(services) };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Qalam could not start:\n\n" + ex.Message, "Qalam", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        UiHelpers.ShowError(e.Exception);
        e.Handled = true;
    }
}
