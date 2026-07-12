using System.Runtime.InteropServices;
using System.Windows.Interop;
using NAudio.Vst3;

namespace DrumPracticeStudio.Views;

internal sealed class Vst3EditorHost : HwndHost
{
    private const string ContainerClassName = "DrumPracticeStudio.Vst3EditorHost";
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipChildren = 0x02000000;
    private const int ColorButtonFace = 15;
    private static readonly IntPtr IdcArrow = 32512;
    private static readonly object ClassRegistrationLock = new();
    private static bool _classRegistered;
    private static WndProcDelegate? _containerWindowProcedure;

    private readonly Vst3PluginView _view;
    private IntPtr _container;
    private float _dpiScale = 1f;

    public Vst3EditorHost(Vst3PluginView view)
    {
        _view = view;
        _view.Resized += OnPluginResized;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var dpi = GetDpiForWindow(hwndParent.Handle);
        _dpiScale = dpi > 0 ? dpi / 96f : 1f;
        EnsureContainerClassRegistered();
        _container = CreateWindowEx(
            0,
            ContainerClassName,
            null,
            WsChild | WsVisible | WsClipChildren,
            0,
            0,
            100,
            100,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_container == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"No se pudo crear la ventana del instrumento VST3 (error {Marshal.GetLastWin32Error()}).");
        }

        _view.AttachTo(_container, _dpiScale);
        ApplySize(_view.GetSize());
        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _view.Detach();
        if (_container != IntPtr.Zero)
        {
            DestroyWindow(_container);
            _container = IntPtr.Zero;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _view.Resized -= OnPluginResized;
        }

        base.Dispose(disposing);
    }

    private static void EnsureContainerClassRegistered()
    {
        if (_classRegistered)
        {
            return;
        }

        lock (ClassRegistrationLock)
        {
            if (_classRegistered)
            {
                return;
            }

            _containerWindowProcedure = (window, message, wParam, lParam) =>
                DefWindowProc(window, message, wParam, lParam);
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_containerWindowProcedure),
                Instance = GetModuleHandle(null),
                Cursor = LoadCursor(IntPtr.Zero, IdcArrow),
                Background = ColorButtonFace + 1,
                ClassName = ContainerClassName
            };
            if (RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException(
                    $"No se pudo registrar la ventana VST3 (error {Marshal.GetLastWin32Error()}).");
            }

            _classRegistered = true;
        }
    }

    private void OnPluginResized(object? sender, Vst3ViewSize size)
    {
        if (!CheckAccess())
        {
            Dispatcher.Invoke(() => ApplySize(size));
            return;
        }

        ApplySize(size);
    }

    private void ApplySize(Vst3ViewSize size)
    {
        Width = _dpiScale > 0 ? size.Width / _dpiScale : size.Width;
        Height = _dpiScale > 0 ? size.Height / _dpiScale : size.Height;
    }

    private delegate IntPtr WndProcDelegate(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string? windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
