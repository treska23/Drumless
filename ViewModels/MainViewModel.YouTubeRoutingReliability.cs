namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _youtubeRoutingResetGate = new(1, 1);

    public async Task ResetYouTubeAudioRoutingAsync(string? reason = null)
    {
        await _youtubeRoutingResetGate.WaitAsync();
        try
        {
            // StopYouTubeAudioCapture retira la fuente del mezclador de forma inmediata y deja la
            // limpieza pesada en segundo plano. Esperar a esta llamada evita que un stop antiguo
            // alcance y elimine la captura recién creada para el vídeo siguiente.
            await Task.Run(_audio.StopYouTubeAudioCapture);

            if (_isYouTubeAudioRouted)
            {
                _isYouTubeAudioRouted = false;
                OnPropertyChanged(nameof(IsYouTubeAudioRouted));
                OnPropertyChanged(nameof(CanStartOutputRecording));
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                RecordingStatus = reason;
            }
        }
        finally
        {
            _youtubeRoutingResetGate.Release();
        }
    }
}
