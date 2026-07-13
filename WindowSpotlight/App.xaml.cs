using System.Windows;
using System.Windows.Threading;

namespace WindowSpotlight;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SessionEnding += OnSessionEnding;
    }

    private static void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        (Current.MainWindow as MainWindow)?.RestoreTargetWindow();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        (Current.MainWindow as MainWindow)?.RestoreTargetWindow();
        MessageBox.Show(
            $"予期しないエラーが発生しました。\n\n{e.Exception.Message}",
            "WindowSpotlight",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
