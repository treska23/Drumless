using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // NAudio 3 separa varias implementaciones Windows en ensamblados opcionales. AudioFileReader
        // resuelve Media Foundation en tiempo de ejecución; si NAudio.Wasapi todavía no ha sido cargado
        // en el proceso puede interpretar erróneamente que el paquete no está instalado aunque sí esté
        // referenciado y copiado junto al ejecutable. Lo cargamos explícitamente antes de abrir pistas.
        EnsureWindowsAudioAssembliesLoaded();

        if (e.Args.Length == 2 &&
            string.Equals(
                e.Args[0],
                ProcessLoopbackCaptureProtocol.Argument,
                StringComparison.Ordinal) &&
            uint.TryParse(e.Args[1], out var rootProcessId))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ProcessLoopbackCaptureProtocol.Start(rootProcessId);
            return;
        }

        if (e.Args.Length == 3 &&
            string.Equals(e.Args[0], Vst3ProbeProtocol.Argument, StringComparison.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = Vst3ProbeProtocol.Execute(e.Args[1], e.Args[2]);
            Shutdown(exitCode);
            return;
        }

        if (e.Args.Length == 2 &&
            string.Equals(
                e.Args[0],
                Vst3ParameterProbeProtocol.Argument,
                StringComparison.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = Vst3ParameterProbeProtocol.Execute(e.Args[1]);
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

        if (e.Args.Length == 2 &&
            string.Equals(e.Args[0], Vst3EffectRuntimeProtocol.Argument, StringComparison.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Vst3EffectRuntimeProtocol.Start(e.Args[1]);
            return;
        }

        base.OnStartup(e);
        EnsurePythonCompatibilityPath();
        EnsureSafeMixerKnobStyle();
        FreezeThemeBrushes();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static void EnsurePythonCompatibilityPath()
    {
        // Los procesos Python heredan PYTHONPATH. sitecustomize.py normaliza únicamente
        // argumentos incompatibles entre versiones de Demucs, por ejemplo --segment 7.8
        // cuando la versión instalada exige un entero.
        var scriptsDirectory = Path.Combine(AppContext.BaseDirectory, "scripts");
        var compatibilityScript = Path.Combine(scriptsDirectory, "sitecustomize.py");
        if (!File.Exists(compatibilityScript))
        {
            return;
        }

        var current = Environment.GetEnvironmentVariable("PYTHONPATH");
        var paths = string.IsNullOrWhiteSpace(current)
            ? []
            : current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Any(path => string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(scriptsDirectory),
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var updated = string.IsNullOrWhiteSpace(current)
            ? scriptsDirectory
            : scriptsDirectory + Path.PathSeparator + current;
        Environment.SetEnvironmentVariable("PYTHONPATH", updated);
    }

    private void EnsureSafeMixerKnobStyle()
    {
        // La plantilla XAML original del mando usa recursos anidados y puede fallar al resolverse
        // durante InitializeComponent. Sustituimos el recurso antes de construir MainWindow por un
        // estilo funcional que nunca depende de StaticResource internos. Así un fallo cosmético no
        // puede impedir que arranque toda la aplicación.
        var style = new Style(typeof(Slider));
        style.Setters.Add(new Setter(Slider.OrientationProperty, Orientation.Horizontal));
        style.Setters.Add(new Setter(Slider.IsMoveToPointEnabledProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 96d));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 28d));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        Resources["MixerKnob"] = style;
    }

    private static void EnsureWindowsAudioAssembliesLoaded()
    {
        try
        {
            _ = Assembly.Load("NAudio.Wasapi");
            return;
        }
        catch
        {
            // En publicaciones self-contained o layouts no estándar probamos la DLL que acompaña
            // directamente al ejecutable. Si tampoco existe, AudioFileReader mostrará su error normal.
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "NAudio.Wasapi.dll");
            if (File.Exists(path))
            {
                _ = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
        }
        catch
        {
            // No bloqueamos el arranque: WAV/AIFF y otras rutas que no usan Media Foundation
            // pueden seguir funcionando aunque la instalación de NAudio esté incompleta.
        }
    }

    private void FreezeThemeBrushes()
    {
        // Los pinceles de tema son recursos compartidos. Congelarlos evita que un control, una
        // animación o una ventana nativa que reciba la misma instancia pueda mutar su Color y hacer
        // que otros controles cambien de aspecto después de llevar un rato usando la aplicación.
        foreach (var brush in Resources.Values.OfType<SolidColorBrush>())
        {
            if (brush.CanFreeze && !brush.IsFrozen)
            {
                brush.Freeze();
            }
        }
    }
}