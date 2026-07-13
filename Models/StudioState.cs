using System.Collections.ObjectModel;
using DrumPracticeStudio.Infrastructure;

namespace DrumPracticeStudio.Models;

public enum PlaybackMode
{
    Single,
    Sequential,
    Shuffle
}

public sealed class StudioState
{
    public string OutputFolder { get; set; } = string.Empty;
    public string? AudioOutputDeviceId { get; set; }
    public string? AudioInputOutputDeviceId { get; set; }
    public int? AudioInputChannelIndex { get; set; }
    public double AudioInputGain { get; set; } = 0.8d;
    public string? MidiDeviceName { get; set; }
    public int? MidiDeviceIndex { get; set; }
    public bool AutoConnectMidi { get; set; } = true;
    public double MidiVelocitySensitivity { get; set; } = 72d;
    public string? ActiveLibraryId { get; set; }
    public string? ActiveKitId { get; set; }
    public double TrackVolume { get; set; } = 0.8d;
    public string? VstModulePath { get; set; }
    public string? VstClassId { get; set; }
    public bool AutoLoadVst { get; set; }
    public List<TrackRecord> Tracks { get; set; } = [];
    public List<Playlist> Playlists { get; set; } = [];
    public string? SelectedPlaylistId { get; set; }
    public PlaybackMode PlaybackMode { get; set; } =
        global::DrumPracticeStudio.Models.PlaybackMode.Sequential;
}

public sealed class TrackRecord
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Path { get; init; }
    public required TrackVariant Variant { get; init; }
}

public sealed class Playlist : ObservableObject
{
    private string _name = string.Empty;

    public required string Id { get; init; }

    public required string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public ObservableCollection<string> TrackIds { get; } = [];
}
