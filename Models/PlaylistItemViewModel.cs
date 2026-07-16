using DrumPracticeStudio.Infrastructure;

namespace DrumPracticeStudio.Models;

public sealed class PlaylistItemViewModel : ObservableObject
{
    public required PlaylistItem Item { get; init; }
    public LocalTrack? LocalTrack { get; init; }

    public string Id => Item.Id;
    public PlaylistItemKind Kind => Item.Kind;
    public string Title => LocalTrack?.Title ?? Item.Title;
    public string SourceLabel => Kind == PlaylistItemKind.YouTube
        ? "YouTube"
        : LocalTrack?.VariantLabel ?? "Pista local";
    public string Detail => Kind == PlaylistItemKind.YouTube
        ? Item.YouTubeUrl ?? "Vídeo de YouTube"
        : LocalTrack?.Path ?? "Pista local no encontrada";
    public bool IsMissing => Kind == PlaylistItemKind.LocalTrack &&
                             (LocalTrack is null || LocalTrack.IsMissing);
    public bool IsAvailable => !IsMissing;
    public string AvailabilityLabel => IsMissing ? "No disponible" : SourceLabel;
}
