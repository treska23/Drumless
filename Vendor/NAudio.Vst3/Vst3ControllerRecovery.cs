using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NAudio.Vst3.Hosting;
using NAudio.Vst3.Interop;

namespace NAudio.Vst3;

/// <summary>
/// Compatibility recovery for older/shell-style VST3 plug-ins whose component returns a usable
/// controller class id but reports a non-Ok result code, or whose controller is exported as a
/// separate factory class. This is primarily needed by some WaveShell generations.
/// </summary>
public static class Vst3ControllerRecovery
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static unsafe bool TryRecoverForEditor(Vst3Plugin plugin, Vst3Module module)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(module);

        if (plugin.HasEditController)
        {
            return true;
        }

        var component = GetField<IComponent>(plugin, "_component");
        var factory = GetField<IPluginFactory>(module, "_factory");
        if (component is null || factory is null)
        {
            return false;
        }

        var candidateIds = new List<string>();

        // Some shell plug-ins fill the controller CID correctly but return kResultFalse instead of Ok.
        // The old host discarded the filled CID solely because of the return code. Trust a non-zero CID.
        Span<byte> advertisedCid = stackalloc byte[16];
        advertisedCid.Clear();
        fixed (byte* cidPtr = advertisedCid)
        {
            _ = component.GetControllerClassId((IntPtr)cidPtr);
        }
        if (advertisedCid.IndexOfAnyExcept((byte)0) >= 0)
        {
            candidateIds.Add(Convert.ToHexString(advertisedCid));
        }

        // Secondary compatibility path: factories such as WaveShell may publish a distinct controller
        // class. Only consider controller classes whose normalized name matches this processor, so a
        // shell containing many Waves plug-ins can never attach another plug-in's editor by accident.
        var processorName = NormalizeName(plugin.ClassInfo.Name);
        foreach (var candidate in module.GetClasses()
                     .Where(candidate =>
                         candidate.Category.Contains("Controller", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(candidate => NameMatchScore(processorName, NormalizeName(candidate.Name))))
        {
            var candidateName = NormalizeName(candidate.Name);
            if (NameMatchScore(processorName, candidateName) <= 0)
            {
                continue;
            }
            candidateIds.Add(candidate.ClassId);
        }

        // Last fallback retained from the original host for plug-ins that use one CID for both objects.
        candidateIds.Add(plugin.ClassInfo.ClassId);

        foreach (var classId in candidateIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryInstallController(plugin, factory, classId))
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe bool TryInstallController(
        Vst3Plugin plugin,
        IPluginFactory factory,
        string controllerClassId)
    {
        byte[] cidBytes;
        try
        {
            cidBytes = Convert.FromHexString(controllerClassId);
        }
        catch
        {
            return false;
        }
        if (cidBytes.Length != 16)
        {
            return false;
        }

        var iidBytes = Vst3StandardInterfaceIds.IEditController.ToByteArray(bigEndian: false);
        IntPtr controllerPtr;
        fixed (byte* cidPtr = cidBytes)
        fixed (byte* iidPtr = iidBytes)
        {
            var createHr = factory.CreateInstance((IntPtr)cidPtr, (IntPtr)iidPtr, out controllerPtr);
            if (createHr != TResultCodes.Ok || controllerPtr == IntPtr.Zero)
            {
                return false;
            }
        }

        var controllerIid = Vst3StandardInterfaceIds.IEditController;
        var qiHr = Marshal.QueryInterface(controllerPtr, in controllerIid, out var verifiedControllerPtr);
        Marshal.Release(controllerPtr);
        if (qiHr != 0 || verifiedControllerPtr == IntPtr.Zero)
        {
            return false;
        }

        IEditController? controller = null;
        try
        {
            controller = (IEditController)Vst3ComWrappers.Instance.GetOrCreateObjectForComInstance(
                verifiedControllerPtr,
                CreateObjectFlags.UniqueInstance);

            var hostUnknown = GetField<IntPtr>(plugin, "_hostUnknown");
            var initHr = controller.Initialize(hostUnknown);
            if (initHr != TResultCodes.Ok)
            {
                ((ComObject)(object)controller).FinalRelease();
                return false;
            }

            SetField(plugin, "_controller", controller);
            SetField(plugin, "_hasSeparateController", true);
            SetField(plugin, "_controllerInitialized", true);

            var connectionIid = Vst3StandardInterfaceIds.IConnectionPoint;
            Marshal.QueryInterface(verifiedControllerPtr, in connectionIid, out var controllerCpPtr);
            SetField(plugin, "_controllerCpPtr", controllerCpPtr);

            var midiIid = Vst3StandardInterfaceIds.IMidiMapping;
            Marshal.QueryInterface(verifiedControllerPtr, in midiIid, out var midiMappingPtr);
            SetField(plugin, "_midiMappingPtr", midiMappingPtr);

            var unitInfoIid = Vst3StandardInterfaceIds.IUnitInfo;
            Marshal.QueryInterface(verifiedControllerPtr, in unitInfoIid, out var unitInfoPtr);
            SetField(plugin, "_unitInfoPtr", unitInfoPtr);

            InstallComponentHandler(plugin, controller);
            InvokePrivate(plugin, "TryConnectComponentAndController");
            InvokePrivate(plugin, "SyncComponentStateToController");
            InitialiseLateParameterSupport(plugin);
            return true;
        }
        catch
        {
            if (controller is not null)
            {
                try
                {
                    ((ComObject)(object)controller).FinalRelease();
                }
                catch
                {
                }
            }
            SetField(plugin, "_controller", null);
            SetField(plugin, "_hasSeparateController", false);
            SetField(plugin, "_controllerInitialized", false);
            return false;
        }
        finally
        {
            Marshal.Release(verifiedControllerPtr);
        }
    }

    private static void InstallComponentHandler(Vst3Plugin plugin, IEditController controller)
    {
        if (GetField<IntPtr>(plugin, "_componentHandlerUnknown") != IntPtr.Zero)
        {
            return;
        }

        var restartMethod = typeof(Vst3Plugin).GetMethod("OnRestartComponent", PrivateInstance);
        var handler = new Vst3ComponentHandler(
            (id, value) => plugin.SetParameterNormalized(id, value),
            flags => restartMethod?.Invoke(plugin, [flags]));

        var identity = Vst3ComWrappers.Instance.GetOrCreateComInterfaceForObject(
            handler,
            CreateComInterfaceFlags.None);
        try
        {
            var handlerIid = Vst3StandardInterfaceIds.IComponentHandler;
            var hr = Marshal.QueryInterface(identity, in handlerIid, out var handlerPtr);
            if (hr != 0 || handlerPtr == IntPtr.Zero)
            {
                return;
            }
            SetField(plugin, "_componentHandlerUnknown", handlerPtr);
            controller.SetComponentHandler(handlerPtr);
        }
        finally
        {
            Marshal.Release(identity);
        }
    }

    private static void InitialiseLateParameterSupport(Vst3Plugin plugin)
    {
        try
        {
            var buildParameters = typeof(Vst3Plugin).GetMethod("BuildParameterCollection", PrivateInstance);
            var parameters = buildParameters?.Invoke(plugin, null);
            if (parameters is not null)
            {
                typeof(Vst3Plugin)
                    .GetProperty(nameof(Vst3Plugin.Parameters), BindingFlags.Instance | BindingFlags.Public)
                    ?.SetValue(plugin, parameters);
            }

            InvokePrivate(plugin, "BuildMidiControllerMap");
            InvokePrivate(plugin, "CacheProgramChangeParameter");
            InvokePrivate(plugin, "BuildUnitModel");

            if (GetField<Vst3HostParameterChanges>(plugin, "_inputChanges") is not null)
            {
                return;
            }

            var inputChanges = new Vst3HostParameterChanges();
            var identity = Vst3ComWrappers.Instance.GetOrCreateComInterfaceForObject(
                inputChanges,
                CreateComInterfaceFlags.None);
            try
            {
                var iid = Vst3StandardInterfaceIds.IParameterChanges;
                var hr = Marshal.QueryInterface(identity, in iid, out var inputChangesPtr);
                if (hr == 0 && inputChangesPtr != IntPtr.Zero)
                {
                    SetField(plugin, "_inputChanges", inputChanges);
                    SetField(plugin, "_inputChangesPtr", inputChangesPtr);
                }
            }
            finally
            {
                Marshal.Release(identity);
            }
        }
        catch
        {
            // The recovered controller can still provide a native editor even if generic host-side
            // parameter enumeration is not available. Do not discard a working editor for that.
        }
    }

    private static int NameMatchScore(string processorName, string controllerName)
    {
        if (string.IsNullOrEmpty(processorName) || string.IsNullOrEmpty(controllerName))
        {
            return 0;
        }
        if (string.Equals(processorName, controllerName, StringComparison.Ordinal))
        {
            return 3;
        }
        if (controllerName.Contains(processorName, StringComparison.Ordinal) ||
            processorName.Contains(controllerName, StringComparison.Ordinal))
        {
            return 2;
        }
        return 0;
    }

    private static string NormalizeName(string value)
    {
        var normalized = new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        foreach (var suffix in new[] { "controller", "editcontroller", "componentcontroller" })
        {
            normalized = normalized.Replace(suffix, string.Empty, StringComparison.Ordinal);
        }
        return normalized;
    }

    private static T? GetField<T>(object instance, string name)
    {
        var value = instance.GetType().GetField(name, PrivateInstance)?.GetValue(instance);
        if (value is null)
        {
            return default;
        }
        return (T)value;
    }

    private static void SetField(object instance, string name, object? value) =>
        instance.GetType().GetField(name, PrivateInstance)?.SetValue(instance, value);

    private static void InvokePrivate(object instance, string methodName) =>
        instance.GetType().GetMethod(methodName, PrivateInstance)?.Invoke(instance, null);
}
