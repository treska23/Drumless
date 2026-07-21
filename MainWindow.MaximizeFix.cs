using System.Windows;
using System.Windows.Threading;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    /// <summary>
    /// FitToCurrentMonitor limita MaxWidth/MaxHeight mientras la ventana está en estado Normal para
    /// mantenerla dentro del área de trabajo y respetar el escalado DPI. Esos límites no deben seguir
    /// activos al maximizar: WPF los aplica también al estado Maximized y deja una franja del escritorio
    /// visible alrededor de la aplicación.
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
        }

        base.OnStateChanged(e);

        if (WindowState == WindowState.Normal)
        {
            // Al restaurar, volvemos a aplicar el ajuste que mantiene la ventana normal dentro del
            // monitor actual. BeginInvoke deja que WPF restaure primero sus RestoreBounds.
            Dispatcher.BeginInvoke(
                () => FitToCurrentMonitor(center: false),
                DispatcherPriority.Loaded);
        }
    }
}
