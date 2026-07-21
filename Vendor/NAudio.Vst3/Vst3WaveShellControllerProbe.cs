using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NAudio.Vst3.Interop;

namespace NAudio.Vst3;

/// <summary>
/// Recuperación específica para shells VST3 que sólo entregan correctamente el CID de su
/// IEditController mientras el componente está en estado Created. La especificación VST3 indica
/// que IComponent::getControllerClassId se consulta antes de initialize().
/// </summary>
public static class Vst3WaveShellControllerProbe
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    public static unsafe bool TryRecover(
        Vst3Plugin plugin,
        Vst3Module module,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(module);

        var factory = typeof(Vst3Module)
            .GetField("_factory", PrivateInstance)
            ?.GetValue(module) as IPluginFactory;
        if (factory is null)
        {
            diagnostic = "no se pudo acceder a IPluginFactory";
            return false;
        }

        Span<byte> componentCid = stackalloc byte[16];
        try
        {
            Vst3Tuid.Parse(plugin.ClassInfo.ClassId, componentCid);
        }
        catch (Exception exception)
        {
            diagnostic = $"ClassID de componente inválido: {exception.Message}";
            return false;
        }

        var componentIid = Vst3StandardInterfaceIds.IComponent.ToByteArray(bigEndian: false);
        IntPtr componentPtr;
        int createHr;
        fixed (byte* cidPtr = componentCid)
        fixed (byte* iidPtr = componentIid)
        {
            createHr = factory.CreateInstance((IntPtr)cidPtr, (IntPtr)iidPtr, out componentPtr);
        }
        if (createHr != TResultCodes.Ok || componentPtr == IntPtr.Zero)
        {
            diagnostic = $"no se pudo crear componente temporal (HRESULT 0x{createHr:X8})";
            return false;
        }

        IComponent? temporaryComponent = null;
        try
        {
            temporaryComponent = (IComponent)Vst3ComWrappers.Instance.GetOrCreateObjectForComInstance(
                componentPtr,
                CreateObjectFlags.UniqueInstance);
            Marshal.Release(componentPtr);
            componentPtr = IntPtr.Zero;

            // Importante: NO inicializar este componente. WaveShell debe recibir esta consulta en
            // estado Created, antes de IPluginBase::initialize.
            Span<byte> controllerCid = stackalloc byte[16];
            controllerCid.Clear();
            int cidHr;
            fixed (byte* controllerCidPtr = controllerCid)
            {
                cidHr = temporaryComponent.GetControllerClassId((IntPtr)controllerCidPtr);
            }

            if (controllerCid.IndexOfAnyExcept((byte)0) < 0)
            {
                diagnostic = $"el componente temporal no entregó CID de controlador (HRESULT 0x{cidHr:X8})";
                return false;
            }

            var controllerClassId = Convert.ToHexString(controllerCid);
            var installMethod = typeof(Vst3ControllerRecovery).GetMethod(
                "TryInstallController",
                PrivateStatic);
            if (installMethod is null)
            {
                diagnostic = "no se encontró la rutina interna de instalación del controlador";
                return false;
            }

            bool installed;
            try
            {
                installed = installMethod.Invoke(
                    null,
                    [plugin, factory, controllerClassId, true]) is true;
            }
            catch (TargetInvocationException exception)
            {
                diagnostic = $"falló la instalación del controlador: {exception.InnerException?.Message ?? exception.Message}";
                return false;
            }

            diagnostic = installed
                ? $"controlador recuperado desde estado Created (CID {controllerClassId}, HRESULT 0x{cidHr:X8})"
                : $"el CID {controllerClassId} se obtuvo antes de initialize, pero no pudo instalarse";
            return installed;
        }
        finally
        {
            if (componentPtr != IntPtr.Zero)
            {
                Marshal.Release(componentPtr);
            }
            if (temporaryComponent is not null)
            {
                try
                {
                    ((ComObject)(object)temporaryComponent).FinalRelease();
                }
                catch
                {
                }
            }
        }
    }
}
