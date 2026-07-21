using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    /// <summary>
    /// La ventana normal se mantiene dentro del monitor mediante FitToCurrentMonitor. Al maximizar,
    /// sin embargo, los MaxWidth/MaxHeight calculados para ese estado normal pueden seguir influyendo
    /// en el tamaño final y dejar visible el margen de 12 DIP por la derecha y por abajo.
    ///
    /// Por eso, al entrar en Maximized eliminamos esos límites y, una vez WPF ha completado el cambio
    /// de estado, colocamos explícitamente la ventana sobre TODO el área de trabajo del monitor actual.
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
        }

        base.OnStateChanged(e);

        if (WindowState == WindowState.Maximized)
        {
            // WPF/Windows calcula primero los límites de maximizado. Ejecutarlo después evita que el
            // MaxWidth/MaxHeight que se usó en estado Normal deje las franjas del escritorio visibles.
            Dispatcher.BeginInvoke(
                ApplyExactMaximizedBounds,
                DispatcherPriority.Loaded);
        }
        else if (WindowState == WindowState.Normal)
        {
            // Al restaurar, volvemos a aplicar el ajuste que mantiene la ventana normal dentro del
            // monitor actual. BeginInvoke deja que WPF restaure primero sus RestoreBounds.
            Dispatcher.BeginInvoke(
                () => FitToCurrentMonitor(center: false),
                DispatcherPriority.Loaded);
        }
    }

    private void ApplyExactMaximizedBounds()
    {
        if (WindowState != WindowState.Maximized)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var work = monitorInfo.Work;
        var width = Math.Max(1, work.Right - work.Left);
        var height = Math.Max(1, work.Bottom - work.Top);

        // Sin el margen usado por FitToCurrentMonitor: en modo maximizado debe rellenar el área útil
        // completa del monitor (la barra de tareas queda respetada por monitorInfo.Work).
        SetWindowPos(
            handle,
            IntPtr.Zero,
            work.Left,
            work.Top,
            width,
            height,
            SwpNoZOrder | SwpNoActivate);
    }
}
