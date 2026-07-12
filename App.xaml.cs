using System.Windows;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 3 &&
            string.Equals(e.Args[0], Vst3ProbeProtocol.Argument, StringComparison.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = Vst3ProbeProtocol.Execute(e.Args[1], e.Args[2]);
            Shutdown(exitCode);
            return;
        }

        if (e.Args.Length == 3 &&
            string.Equals(e.Args[0], Vst3RuntimeProtocol.Argument, StringComparison.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Vst3RuntimeProtocol.Start(e.Args[1], e.Args[2]);
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
