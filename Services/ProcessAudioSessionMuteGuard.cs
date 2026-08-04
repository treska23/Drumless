using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DrumPracticeStudio.Services;

/// <summary>
/// Mantiene silenciadas en el mezclador de Windows las sesiones de render pertenecientes a un
/// proceso y a sus descendientes. El proceso sigue renderizando: la captura loopback por proceso
/// puede leer el flujo antes del volumen/mute de la sesión y Drumless lo envía después a su salida.
/// </summary>
internal sealed class ProcessAudioSessionMuteGuard : IAsyncDisposable
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private const uint DeviceStateActive = 0x00000001;
    private const uint ClsctxAll = 0x17;
    private const uint CoinitMultithreaded = 0x0;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly Guid AudioSessionManager2Id =
        new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid MuteEventContext =
        new("5BA682F2-82B8-4F9D-842D-25C9F4D3145B");

    private readonly uint _rootProcessId;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<string, bool> _originalMuteStates =
        new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _firstProtectedSession =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _monitorTask;
    private int _disposeStarted;

    private ProcessAudioSessionMuteGuard(uint rootProcessId)
    {
        _rootProcessId = rootProcessId;
        _monitorTask = Task.Run(() => MonitorAsync(_cancellation.Token));
    }

    public uint RootProcessId => _rootProcessId;

    public static ProcessAudioSessionMuteGuard Start(uint rootProcessId)
    {
        if (rootProcessId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rootProcessId));
        }

        return new ProcessAudioSessionMuteGuard(rootProcessId);
    }

    public async Task<bool> WaitUntilProtectedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _firstProtectedSession.Task
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var comResult = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
        var uninitializeCom = comResult >= 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var protectedSessions = 0;
                try
                {
                    protectedSessions = VisitMatchingSessions(
                        (sessionId, volume) =>
                        {
                            ThrowIfFailed(volume.GetMute(out var originallyMuted));
                            _originalMuteStates.TryAdd(sessionId, originallyMuted);
                            ThrowIfFailed(volume.SetMute(true, ref MuteEventContext));
                            return true;
                        });
                }
                catch (COMException)
                {
                    // WebView2 y las sesiones de audio se crean y destruyen de forma asíncrona.
                    // La siguiente pasada vuelve a enumerar todo desde cero.
                }
                catch (InvalidCastException)
                {
                    // Una sesión puede desaparecer entre GetSession y QueryInterface.
                }

                if (protectedSessions > 0)
                {
                    _firstProtectedSession.TrySetResult(true);
                }

                await Task.Delay(
                    protectedSessions == 0 ? 75 : 220,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (uninitializeCom)
            {
                CoUninitialize();
            }
        }
    }

    private int VisitMatchingSessions(Func<string, ISimpleAudioVolume, bool> visitor)
    {
        var processTree = GetProcessTree(_rootProcessId);
        object? deviceEnumeratorObject = null;
        IMMDeviceCollection? devices = null;
        var visited = 0;
        try
        {
            deviceEnumeratorObject = new MMDeviceEnumeratorComObject();
            var deviceEnumerator = (IMMDeviceEnumerator)deviceEnumeratorObject;
            ThrowIfFailed(deviceEnumerator.EnumAudioEndpoints(
                EDataFlow.Render,
                DeviceStateActive,
                out devices));
            ThrowIfFailed(devices.GetCount(out var deviceCount));

            for (uint deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                IMMDevice? device = null;
                object? managerObject = null;
                IAudioSessionEnumerator? sessions = null;
                try
                {
                    ThrowIfFailed(devices.Item(deviceIndex, out device));
                    ThrowIfFailed(device.Activate(
                        ref AudioSessionManager2Id,
                        ClsctxAll,
                        IntPtr.Zero,
                        out managerObject));
                    var manager = (IAudioSessionManager2)managerObject;
                    ThrowIfFailed(manager.GetSessionEnumerator(out sessions));
                    ThrowIfFailed(sessions.GetCount(out var sessionCount));

                    for (var sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                    {
                        IAudioSessionControl? session = null;
                        try
                        {
                            ThrowIfFailed(sessions.GetSession(sessionIndex, out session));
                            var session2 = (IAudioSessionControl2)session;
                            ThrowIfFailed(session2.GetProcessId(out var processId));
                            if (!processTree.Contains(processId))
                            {
                                continue;
                            }

                            ThrowIfFailed(session2.GetSessionInstanceIdentifier(out var sessionId));
                            if (string.IsNullOrWhiteSpace(sessionId))
                            {
                                sessionId = $"pid:{processId}:device:{deviceIndex}:session:{sessionIndex}";
                            }

                            var volume = (ISimpleAudioVolume)session;
                            if (visitor(sessionId, volume))
                            {
                                visited++;
                            }
                        }
                        finally
                        {
                            ReleaseComObject(session);
                        }
                    }
                }
                catch (COMException)
                {
                    // Un endpoint o una sesión puede desaparecer durante la enumeración.
                }
                finally
                {
                    ReleaseComObject(sessions);
                    ReleaseComObject(managerObject);
                    ReleaseComObject(device);
                }
            }
        }
        finally
        {
            ReleaseComObject(devices);
            ReleaseComObject(deviceEnumeratorObject);
        }

        return visited;
    }

    private void RestoreOriginalMuteStates()
    {
        if (_originalMuteStates.IsEmpty)
        {
            return;
        }

        var comResult = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
        var uninitializeCom = comResult >= 0;
        try
        {
            try
            {
                VisitMatchingSessions(
                    (sessionId, volume) =>
                    {
                        if (!_originalMuteStates.TryGetValue(sessionId, out var wasMuted))
                        {
                            return false;
                        }

                        ThrowIfFailed(volume.SetMute(wasMuted, ref MuteEventContext));
                        return true;
                    });
            }
            catch (COMException)
            {
                // Al cerrar WebView2 es normal que sus sesiones ya no existan.
            }
            catch (InvalidCastException)
            {
            }
        }
        finally
        {
            if (uninitializeCom)
            {
                CoUninitialize();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        try
        {
            await _monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await Task.Run(RestoreOriginalMuteStates).ConfigureAwait(false);
        _cancellation.Dispose();
    }

    private static HashSet<uint> GetProcessTree(uint rootProcessId)
    {
        var result = new HashSet<uint> { rootProcessId };
        var children = new Dictionary<uint, List<uint>>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>()
            };
            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                if (!children.TryGetValue(entry.ParentProcessId, out var list))
                {
                    list = [];
                    children.Add(entry.ParentProcessId, list);
                }
                list.Add(entry.ProcessId);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var pending = new Queue<uint>();
        pending.Enqueue(rootProcessId);
        while (pending.TryDequeue(out var parent))
        {
            if (!children.TryGetValue(parent, out var directChildren))
            {
                continue;
            }

            foreach (var child in directChildren)
            {
                if (result.Add(child))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return result;
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch (InvalidComObjectException)
            {
            }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum AudioSessionState
    {
        Inactive = 0,
        Active = 1,
        Expired = 2
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            EDataFlow dataFlow,
            uint stateMask,
            out IMMDeviceCollection devices);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0A60758B1A5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint deviceIndex, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(
            ref Guid audioSessionGuid,
            uint streamFlags,
            out IAudioSessionControl sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(
            ref Guid audioSessionGuid,
            uint streamFlags,
            out ISimpleAudioVolume simpleAudioVolume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl sessionControl);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        [PreserveSig]
        int SetDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            ref Guid eventContext);

        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        [PreserveSig]
        int SetIconPath(
            [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
            ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr client);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2 : IAudioSessionControl
    {
        [PreserveSig]
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);

        [PreserveSig]
        int GetSessionInstanceIdentifier(
            [MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceIdentifier);

        [PreserveSig]
        int GetProcessId(out uint processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolume(out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
