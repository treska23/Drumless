using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DrumPracticeStudio.ViewModels;

namespace DrumPracticeStudio;

public partial class MainWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;
    private const int WmExitSizeMove = 0x0232;
    private const double WorkAreaMarginDip = 12;

    private readonly MainViewModel _viewModel;
    private HwndSource? _windowSource;
    private bool _isFittingWindow;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        FitToCurrentMonitor(center: true);
    }

    private IntPtr WindowMessageHook(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message is WmDpiChanged or WmDisplayChange or WmExitSizeMove)
        {
            Dispatcher.BeginInvoke(() => FitToCurrentMonitor(center: false));
        }

        return IntPtr.Zero;
    }

    private void FitToCurrentMonitor(bool center)
    {
        if (_isFittingWindow || WindowState != WindowState.Normal)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        _isFittingWindow = true;
        try
        {
            var dpi = GetDpiForWindow(handle);
            var dpiScale = dpi > 0 ? dpi / 96d : 1d;
            var margin = Math.Max(1, (int)Math.Round(WorkAreaMarginDip * dpiScale));
            var workWidth = monitorInfo.Work.Right - monitorInfo.Work.Left;
            var workHeight = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
            var availableWidth = Math.Max(1, workWidth - (margin * 2));
            var availableHeight = Math.Max(1, workHeight - (margin * 2));

            MaxWidth = availableWidth / dpiScale;
            MaxHeight = availableHeight / dpiScale;

            var width = Math.Min(windowRect.Right - windowRect.Left, availableWidth);
            var height = Math.Min(windowRect.Bottom - windowRect.Top, availableHeight);
            var left = center
                ? monitorInfo.Work.Left + ((workWidth - width) / 2)
                : Math.Clamp(
                    windowRect.Left,
                    monitorInfo.Work.Left + margin,
                    monitorInfo.Work.Right - margin - width);
            var top = center
                ? monitorInfo.Work.Top + ((workHeight - height) / 2)
                : Math.Clamp(
                    windowRect.Top,
                    monitorInfo.Work.Top + margin,
                    monitorInfo.Work.Bottom - margin - height);

            SetWindowPos(
                handle,
                IntPtr.Zero,
                left,
                top,
                width,
                height,
                SwpNoZOrder | SwpNoActivate);
        }
        finally
        {
            _isFittingWindow = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _viewModel.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
