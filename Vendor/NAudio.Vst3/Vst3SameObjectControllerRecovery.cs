using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NAudio.Vst3.Interop;

namespace NAudio.Vst3;

/// <summary>
/// Compatibility path for plug-ins that do not advertise a separate controller class but expose
/// IEditController from the live processor/component COM identity. Steinberg explicitly requires
/// hosts to check the audio processor for IEditController when no separate controller is available.
/// </summary>
public static class Vst3SameObjectControllerRecovery
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    public static bool TryRecover(Vst3Plugin plugin, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var processor = GetField<IAudioProcessor>(plugin, "_processor");
        var component = GetField<IComponent>(plugin, "_component");

        if (TryRecoverFromNativeWrapper(plugin, processor, "IAudioProcessor", out diagnostic))
        {
            return true;
        }

        if (TryRecoverFromNativeWrapper(plugin, component, "IComponent", out diagnostic))
        {
            return true;
        }

        diagnostic = "ni IAudioProcessor ni IComponent expusieron IEditController mediante QueryInterface";
        return false;
    }

    private static bool TryRecoverFromNativeWrapper(
        Vst3Plugin plugin,
        object? nativeWrapper,
        string source,
        out string diagnostic)
    {
        if (nativeWrapper is null)
        {
            diagnostic = $"{source} no está disponible";
            return false;
        }

        if (!ComWrappers.TryGetComInstance(nativeWrapper, out var unknownPtr) || unknownPtr == IntPtr.Zero)
        {
            diagnostic = $"no se pudo recuperar el puntero COM nativo de {source}";
            return false;
        }

        try
        {
            var controllerIid = Vst3StandardInterfaceIds.IEditController;
            var qiHr = Marshal.QueryInterface(unknownPtr, in controllerIid, out var controllerPtr);
            if (qiHr != 0 || controllerPtr == IntPtr.Zero)
            {
                diagnostic = $"{source}.QueryInterface(IEditController) falló (HRESULT 0x{qiHr:X8})";
                return false;
            }

            try
            {
                var recoveredController =
                    (IEditController)Vst3ComWrappers.Instance.GetOrCreateObjectForComInstance(
                        controllerPtr,
                        CreateObjectFlags.UniqueInstance);

                var oldController = GetField<IEditController>(plugin, "_controller");
                var oldWasSeparate = GetField<bool>(plugin, "_hasSeparateController");
                var oldWasInitialized = GetField<bool>(plugin, "_controllerInitialized");
                var oldControllerCpPtr = GetField<IntPtr>(plugin, "_controllerCpPtr");
                var oldMidiMappingPtr = GetField<IntPtr>(plugin, "_midiMappingPtr");
                var oldUnitInfoPtr = GetField<IntPtr>(plugin, "_unitInfoPtr");

                if (oldWasSeparate && oldController is not null)
                {
                    InvokePrivateStatic(
                        typeof(Vst3ControllerRecovery),
                        "DisconnectOldController",
                        plugin,
                        oldControllerCpPtr);
                }

                SetField(plugin, "_controller", recoveredController);
                SetField(plugin, "_hasSeparateController", false);
                // This is the same native object as the already-initialized component/processor.
                // Calling IPluginBase::initialize a second time would be invalid.
                SetField(plugin, "_controllerInitialized", false);
                SetField(plugin, "_connected", false);
                SetField(plugin, "_controllerCpPtr", IntPtr.Zero);

                CacheOptionalControllerInterfaces(plugin, controllerPtr);

                InvokePrivateStatic(
                    typeof(Vst3ControllerRecovery),
                    "InstallComponentHandler",
                    plugin,
                    recoveredController);
                InvokePrivateStatic(
                    typeof(Vst3ControllerRecovery),
                    "InitialiseLateParameterSupport",
                    plugin);

                if (oldWasSeparate && oldController is not null &&
                    !ReferenceEquals(oldController, recoveredController))
                {
                    InvokePrivateStatic(
                        typeof(Vst3ControllerRecovery),
                        "ReleaseOldController",
                        oldController,
                        oldWasInitialized,
                        oldControllerCpPtr,
                        oldMidiMappingPtr,
                        oldUnitInfoPtr);
                }

                diagnostic = $"IEditController recuperado directamente desde {source} mediante QueryInterface";
                return true;
            }
            finally
            {
                Marshal.Release(controllerPtr);
            }
        }
        catch (Exception exception)
        {
            diagnostic = $"falló la recuperación desde {source}: {exception.GetBaseException().Message}";
            return false;
        }
        finally
        {
            // TryGetComInstance returns an AddRef'd native pointer owned by the caller.
            Marshal.Release(unknownPtr);
        }
    }

    private static void CacheOptionalControllerInterfaces(Vst3Plugin plugin, IntPtr controllerPtr)
    {
        var midiIid = Vst3StandardInterfaceIds.IMidiMapping;
        if (Marshal.QueryInterface(controllerPtr, in midiIid, out var midiPtr) == 0 && midiPtr != IntPtr.Zero)
        {
            SetField(plugin, "_midiMappingPtr", midiPtr);
        }

        var unitIid = Vst3StandardInterfaceIds.IUnitInfo;
        if (Marshal.QueryInterface(controllerPtr, in unitIid, out var unitPtr) == 0 && unitPtr != IntPtr.Zero)
        {
            SetField(plugin, "_unitInfoPtr", unitPtr);
        }
    }

    private static T? GetField<T>(object instance, string name)
    {
        var value = instance.GetType().GetField(name, PrivateInstance)?.GetValue(instance);
        return value is null ? default : (T)value;
    }

    private static void SetField(object instance, string name, object? value) =>
        instance.GetType().GetField(name, PrivateInstance)?.SetValue(instance, value);

    private static object? InvokePrivateStatic(Type type, string method, params object?[] args) =>
        type.GetMethod(method, PrivateStatic)?.Invoke(null, args);
}
