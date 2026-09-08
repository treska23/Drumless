namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>
    /// Evita cambiar el motor de instrumento mientras hay una grabación iniciándose,
    /// activa o terminándose. Cambiar de VST3 en mitad de una toma puede invalidar
    /// los escritores y dejar la grabación a medias.
    /// </summary>
    private bool TryAllowInstrumentEngineChange()
    {
        if (CanChangeRecordingAudioSetup)
        {
            return true;
        }

        const string message = "Termina la grabación antes de cambiar el instrumento VST3 o el motor de batería.";
        VstStatus = message;
        StatusMessage = message;
        return false;
    }
}
