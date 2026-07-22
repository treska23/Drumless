using System.Text.Json;
using System.Text.Json.Serialization;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class StudioStateStore
{
    public const int CurrentSchemaVersion = 11;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _statePath;

    public StudioStateStore(string? statePath = null)
    {
        var requestedPath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.StudioStatePath
            : statePath;
        _statePath = Path.GetFullPath(requestedPath);
    }

    public string StatePath => _statePath;
    public string? LastLoadWarning { get; private set; }

    public StudioState Load()
    {
        LastLoadWarning = null;
        if (!File.Exists(_statePath))
        {
            return CreateDefaultState();
        }

        try
        {
            using var stream = File.OpenRead(_statePath);
            var document = JsonSerializer.Deserialize<StudioStateDocument>(stream, JsonOptions);
            if (document is null)
            {
                throw new JsonException("El documento de estado está vacío.");
            }

            if (document.SchemaVersion is < 1 or > CurrentSchemaVersion)
            {
                throw new NotSupportedException(
                    $"La versión {document.SchemaVersion} del estado no es compatible.");
            }

            return ToModel(document);
        }
        catch (Exception exception) when (IsRecoverableLoadFailure(exception))
        {
            LastLoadWarning = $"No se pudo recuperar el estado guardado: {exception.Message}";
            return CreateDefaultState();
        }
    }

    public void Save(StudioState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var directory = Path.GetDirectoryName(_statePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("La ruta del estado no tiene un directorio válido.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16_384,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, ToDocument(state), JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Un temporal huérfano no debe ocultar el error de guardado principal.
            }
        }
    }

    private static StudioState CreateDefaultState() => new()
    {
        OutputFolder = AppPaths.DerivedTracks,
        PlaybackMode = PlaybackMode.Sequential
    };

    private static StudioState ToModel(StudioStateDocument document)
    {
        var state = new StudioState
        {
            OutputFolder = string.IsNullOrWhiteSpace(document.OutputFolder)
                ? AppPaths.DerivedTracks
                : document.OutputFolder,
            SelectedPlaylistId = string.IsNullOrWhiteSpace(document.SelectedPlaylistId)
                ? null
                : document.SelectedPlaylistId,
            AudioOutputDeviceId = string.IsNullOrWhiteSpace(document.AudioOutputDeviceId)
                ? null
                : document.AudioOutputDeviceId,
            AudioInputOutputDeviceId = string.IsNullOrWhiteSpace(document.AudioInputOutputDeviceId)
                ? null
                : document.AudioInputOutputDeviceId,
            AudioInputChannelIndex = document.AudioInputChannelIndex is >= 0
                ? document.AudioInputChannelIndex
                : null,
            AudioInputGain = Math.Clamp(document.AudioInputGain ?? 0.8d, 0d, 1.5d),
            Vst3EffectFolders = (document.Vst3EffectFolders ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Vst3EffectCatalog = (document.Vst3EffectCatalog ?? [])
                .Select(TryCreateVst3EffectReference)
                .OfType<Vst3EffectReference>()
                .GroupBy(
                    effect => Vst3EffectItem.GetCatalogId(
                        effect.ModulePath,
                        effect.ClassId),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList(),
            HasScannedVst3Effects = document.HasScannedVst3Effects ?? false,
            Vst3EffectGroupingMode =
                document.Vst3EffectGroupingMode is { } groupingMode &&
                Enum.IsDefined(groupingMode)
                    ? groupingMode
                    : Vst3EffectGroupingMode.VendorThenEffectType,
            MidiDeviceName = string.IsNullOrWhiteSpace(document.MidiDeviceName)
                ? null
                : document.MidiDeviceName,
            MidiDeviceIndex = document.MidiDeviceIndex is >= 0
                ? document.MidiDeviceIndex
                : null,
            AutoConnectMidi = document.AutoConnectMidi ?? true,
            MidiVelocitySensitivity = Math.Clamp(
                document.MidiVelocitySensitivity ?? 72d,
                0d,
                100d),
            ActiveLibraryId = string.IsNullOrWhiteSpace(document.ActiveLibraryId)
                ? null
                : document.ActiveLibraryId,
            ActiveKitId = string.IsNullOrWhiteSpace(document.ActiveKitId)
                ? null
                : document.ActiveKitId,
            TrackVolume = Math.Clamp(document.TrackVolume ?? 0.8d, 0d, 1d),
            VstModulePath = string.IsNullOrWhiteSpace(document.VstModulePath)
                ? null
                : document.VstModulePath,
            VstClassId = string.IsNullOrWhiteSpace(document.VstClassId)
                ? null
                : document.VstClassId,
            AutoLoadVst = document.AutoLoadVst ?? false,
            StemSelection = NormalizeStemSelection(
                document.StemSelection,
                document.SchemaVersion),
            PerformanceLatencyCompensationMs = Math.Clamp(
                document.PerformanceLatencyCompensationMs ?? 0d,
                -500d,
                500d),
            PlaybackMode = Enum.IsDefined(document.PlaybackMode)
                ? document.PlaybackMode
                : PlaybackMode.Sequential
        };

        foreach (var monitor in document.AudioInputMonitors ?? [])
        {
            if (monitor.ChannelIndex is >= 0)
            {
                state.AudioInputMonitors.Add(new AudioInputMonitorSetting(
                    monitor.ChannelIndex.Value,
                    (float)Math.Clamp(monitor.Gain ?? 0.8d, 0d, 1.5d),
                    monitor.Profile is { } profile && Enum.IsDefined(profile)
                        ? profile
                        : AudioInputProfileKind.Clean,
                    monitor.Effects is null
                        ? null
                        : monitor.Effects
                            .Select(TryCreateAudioEffect)
                            .Where(effect => effect is
                            {
                                Kind: AudioEffectKind.ExternalVst3,
                                ExternalVst3: not null
                            })
                            .Cast<AudioEffectSlotSetting>()
                            .Take(AudioEffectCatalog.MaximumSlots)
                            .ToArray(),
                    monitor.EffectsBypassed ?? false));
            }
        }
        if (state.AudioInputMonitors.Count == 0 && state.AudioInputChannelIndex is { } legacyChannel)
        {
            state.AudioInputMonitors.Add(new AudioInputMonitorSetting(
                legacyChannel,
                (float)state.AudioInputGain));
        }

        foreach (var bus in document.AudioEffectBuses ?? [])
        {
            if (!Enum.IsDefined(bus.Target) ||
                state.AudioEffectBuses.Any(existing => existing.Target == bus.Target))
            {
                continue;
            }
            state.AudioEffectBuses.Add(new AudioEffectBusSetting(
                bus.Target,
                (bus.Effects ?? [])
                    .Select(TryCreateAudioEffect)
                    .Where(effect => effect is
                    {
                        Kind: AudioEffectKind.ExternalVst3,
                        ExternalVst3: not null
                    })
                    .Cast<AudioEffectSlotSetting>()
                    .Take(AudioEffectCatalog.MaximumSlots)
                    .ToArray(),
                bus.EffectsBypassed ?? false));
        }

        foreach (var track in document.Tracks ?? [])
        {
            if (string.IsNullOrWhiteSpace(track.Id) ||
                string.IsNullOrWhiteSpace(track.Title) ||
                string.IsNullOrWhiteSpace(track.Path) ||
                !Enum.IsDefined(track.Variant))
            {
                continue;
            }

            state.Tracks.Add(new TrackRecord
            {
                Id = track.Id,
                Title = track.Title,
                Path = track.Path,
                Variant = track.Variant,
                DateAddedUtc = track.DateAddedUtc,
                Tempo = TryCreateTempo(track.Tempo)
            });
        }

        foreach (var playlist in document.Playlists ?? [])
        {
            if (string.IsNullOrWhiteSpace(playlist.Id) || string.IsNullOrWhiteSpace(playlist.Name))
            {
                continue;
            }

            var model = new Playlist
            {
                Id = playlist.Id,
                Name = playlist.Name,
                IsIncludedInMix = playlist.IsIncludedInMix ?? false
            };
            foreach (var item in playlist.Items ?? [])
            {
                if (!TryCreatePlaylistItem(item, out var modelItem))
                {
                    continue;
                }

                model.Items.Add(modelItem);
            }

            // Migración transparente de estados anteriores, que solo guardaban IDs locales.
            if (model.Items.Count == 0)
            {
                foreach (var trackId in playlist.TrackIds ?? [])
                {
                    if (string.IsNullOrWhiteSpace(trackId))
                    {
                        continue;
                    }

                    var title = state.Tracks.FirstOrDefault(track => track.Id == trackId)?.Title ??
                                "Pista local";
                    model.Items.Add(new PlaylistItem
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Kind = PlaylistItemKind.LocalTrack,
                        TrackId = trackId,
                        Title = title
                    });
                }
            }

            state.Playlists.Add(model);
        }

        foreach (var analysis in document.AnalysisRecords ?? [])
        {
            if (TryCreateAnalysisRecord(analysis, out var record) &&
                state.AnalysisRecords.All(existing => existing.MediaKey != record.MediaKey))
            {
                state.AnalysisRecords.Add(record);
            }
        }

        // Los estados anteriores guardaban el tempo dentro de pistas y elementos de playlist.
        // Se importa una sola vez a la base normalizada, que a partir de v3 es la autoridad.
        foreach (var track in state.Tracks.Where(track => track.Tempo is not null))
        {
            ImportLegacyTempo(state, $"local:{track.Id}", track.Tempo!);
        }
        foreach (var item in state.Playlists.SelectMany(playlist => playlist.Items)
                     .Where(item => item.Tempo is not null))
        {
            ImportLegacyTempo(state, item.MediaKey, item.Tempo!);
        }

        HydrateTempoFromAnalysis(state);

        return state;
    }

    private static StudioStateDocument ToDocument(StudioState state) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        OutputFolder = string.IsNullOrWhiteSpace(state.OutputFolder)
            ? AppPaths.DerivedTracks
            : state.OutputFolder,
        SelectedPlaylistId = state.SelectedPlaylistId,
        AudioOutputDeviceId = state.AudioOutputDeviceId,
        AudioInputOutputDeviceId = state.AudioInputOutputDeviceId,
        AudioInputChannelIndex = state.AudioInputChannelIndex,
        AudioInputGain = Math.Clamp(state.AudioInputGain, 0d, 1.5d),
        Vst3EffectFolders = state.Vst3EffectFolders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        Vst3EffectCatalog = state.Vst3EffectCatalog
            .GroupBy(
                effect => Vst3EffectItem.GetCatalogId(
                    effect.ModulePath,
                    effect.ClassId),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => ToVst3EffectReferenceDto(group.First()))
            .ToList(),
        HasScannedVst3Effects = state.HasScannedVst3Effects,
        Vst3EffectGroupingMode = Enum.IsDefined(state.Vst3EffectGroupingMode)
            ? state.Vst3EffectGroupingMode
            : Vst3EffectGroupingMode.VendorThenEffectType,
        AudioInputMonitors = state.AudioInputMonitors.Select(monitor => new AudioInputMonitorDto
        {
            ChannelIndex = monitor.ChannelIndex,
            Gain = Math.Clamp(monitor.Gain, 0f, 1.5f),
            Profile = monitor.Profile,
            EffectsBypassed = monitor.EffectsBypassed,
            Effects = monitor.EffectiveEffects.Select(effect => new AudioEffectSlotDto
            {
                Id = effect.Id,
                Kind = effect.Kind,
                IsEnabled = effect.IsEnabled,
                Amount = effect.Amount,
                Mix = effect.Mix,
                ExternalVst3 = effect.ExternalVst3 is null
                    ? null
                    : new Vst3EffectReferenceDto
                    {
                        ModulePath = effect.ExternalVst3.ModulePath,
                        ModuleName = effect.ExternalVst3.ModuleName,
                        ClassId = effect.ExternalVst3.ClassId,
                        Category = effect.ExternalVst3.Category,
                        Name = effect.ExternalVst3.Name,
                        Vendor = effect.ExternalVst3.Vendor,
                        Version = effect.ExternalVst3.Version,
                        SdkVersion = effect.ExternalVst3.SdkVersion,
                        SubCategories = effect.ExternalVst3.SubCategories,
                        PresetPath = effect.ExternalVst3.PresetPath
                    }
            }).ToList()
        }).ToList(),
        AudioEffectBuses = state.AudioEffectBuses.Select(bus => new AudioEffectBusDto
        {
            Target = bus.Target,
            EffectsBypassed = bus.EffectsBypassed,
            Effects = bus.EffectiveEffects.Select(ToAudioEffectSlotDto).ToList()
        }).ToList(),
        MidiDeviceName = state.MidiDeviceName,
        MidiDeviceIndex = state.MidiDeviceIndex,
        AutoConnectMidi = state.AutoConnectMidi,
        MidiVelocitySensitivity = Math.Clamp(state.MidiVelocitySensitivity, 0d, 100d),
        ActiveLibraryId = state.ActiveLibraryId,
        ActiveKitId = state.ActiveKitId,
        TrackVolume = Math.Clamp(state.TrackVolume, 0d, 1d),
        VstModulePath = state.VstModulePath,
        VstClassId = state.VstClassId,
        AutoLoadVst = state.AutoLoadVst,
        StemSelection = state.StemSelection,
        PerformanceLatencyCompensationMs = Math.Clamp(
            state.PerformanceLatencyCompensationMs,
            -500d,
            500d),
        PlaybackMode = state.PlaybackMode,
        Tracks = state.Tracks.Select(track => new TrackDto
        {
            Id = track.Id,
            Title = track.Title,
            Path = track.Path,
            Variant = track.Variant,
            DateAddedUtc = track.DateAddedUtc,
            Tempo = track.Tempo is null ? null : ToTempoDto(track.Tempo)
        }).ToList(),
        Playlists = state.Playlists.Select(playlist => new PlaylistDto
        {
            Id = playlist.Id,
            Name = playlist.Name,
            IsIncludedInMix = playlist.IsIncludedInMix,
            Items = playlist.Items.Select(item => new PlaylistItemDto
            {
                Id = item.Id,
                Kind = item.Kind,
                TrackId = item.TrackId,
                YouTubeVideoId = item.YouTubeVideoId,
                YouTubeUrl = item.YouTubeUrl,
                Title = item.Title,
                ThumbnailUrl = item.ThumbnailUrl,
                Tempo = item.Tempo is null ? null : ToTempoDto(item.Tempo)
            }).ToList()
        }).ToList(),
        AnalysisRecords = state.AnalysisRecords.Select(ToAnalysisDto).ToList()
    };

    private static void ImportLegacyTempo(
        StudioState state,
        string mediaKey,
        TempoSettings tempo)
    {
        if (string.IsNullOrWhiteSpace(mediaKey) ||
            state.AnalysisRecords.Any(record => record.MediaKey == mediaKey))
        {
            return;
        }

        state.AnalysisRecords.Add(new MediaAnalysisRecord
        {
            MediaKey = mediaKey,
            Tempo = TempoSettings.Normalize(tempo),
            TempoOrigin = tempo.AnalysisConfidence > 0d
                ? TempoAnalysisOrigin.Automatic
                : TempoAnalysisOrigin.Manual,
            TempoUpdatedAtUtc = null
        });
    }

    private static void HydrateTempoFromAnalysis(StudioState state)
    {
        var tempos = state.AnalysisRecords
            .Where(record => record.Tempo is not null)
            .ToDictionary(record => record.MediaKey, record => record.Tempo!, StringComparer.Ordinal);
        foreach (var track in state.Tracks)
        {
            if (tempos.TryGetValue($"local:{track.Id}", out var tempo))
            {
                track.Tempo = tempo;
            }
        }
        foreach (var item in state.Playlists.SelectMany(playlist => playlist.Items))
        {
            item.Tempo = tempos.GetValueOrDefault(item.MediaKey);
        }
    }

    private static bool TryCreateAnalysisRecord(
        MediaAnalysisDto dto,
        out MediaAnalysisRecord record)
    {
        record = null!;
        if (string.IsNullOrWhiteSpace(dto.MediaKey))
        {
            return false;
        }

        var tempo = TryCreateTempo(dto.Tempo);
        var origin = Enum.IsDefined(dto.TempoOrigin)
            ? dto.TempoOrigin
            : TempoAnalysisOrigin.Manual;
        var sessions = new List<DrumPerformanceSession>();
        foreach (var session in dto.PerformanceSessions ?? [])
        {
            if (TryCreatePerformanceSession(session, out var model) &&
                sessions.All(existing => existing.Id != model.Id))
            {
                sessions.Add(model);
            }
        }
        var drumReference = TryCreateDrumReference(dto.DrumReference);
        var songStructure = TryCreateSongStructure(dto.SongStructure);
        var chordSheet = TryCreateChordSheet(dto.ChordSheet);

        record = new MediaAnalysisRecord
        {
            MediaKey = dto.MediaKey,
            Tempo = tempo,
            TempoOrigin = origin,
            TempoUpdatedAtUtc = dto.TempoUpdatedAtUtc,
            SongStructure = songStructure,
            ChordSheet = chordSheet,
            DrumReference = drumReference,
            PerformanceSessions = sessions
        };
        return tempo is not null ||
               songStructure is not null ||
               chordSheet is not null ||
               drumReference is not null ||
               sessions.Count > 0;
    }

    private static SongStructureMap? TryCreateSongStructure(SongStructureDto? dto)
    {
        if (dto is null ||
            dto.AnalyzedAtUtc == default ||
            dto.DurationSeconds is not >= 0d ||
            !double.IsFinite(dto.DurationSeconds.Value) ||
            dto.Confidence is null ||
            !double.IsFinite(dto.Confidence.Value))
        {
            return null;
        }

        var sections = (dto.Sections ?? [])
            .Where(section =>
                !string.IsNullOrWhiteSpace(section.Id) &&
                section.StartSeconds is >= 0d &&
                section.EndSeconds is > 0d &&
                section.EndSeconds > section.StartSeconds &&
                !string.IsNullOrWhiteSpace(section.Label))
            .Select(section => new SongSection(
                section.Id!,
                section.StartSeconds!.Value,
                section.EndSeconds!.Value,
                section.Label!,
                section.Confidence ?? 0d,
                section.Signature ?? string.Empty))
            .ToArray();
        if (sections.Length == 0)
        {
            return null;
        }
        return SongStructureMap.Normalize(new SongStructureMap(
            dto.AnalyzedAtUtc,
            dto.DurationSeconds.Value,
            dto.Confidence.Value,
            sections));
    }

    private static ChordSheetDocument? TryCreateChordSheet(ChordSheetDto? dto)
    {
        if (dto is null ||
            string.IsNullOrWhiteSpace(dto.Id) ||
            string.IsNullOrWhiteSpace(dto.Title) ||
            dto.UpdatedAtUtc == default ||
            !Enum.IsDefined(dto.SourceKind))
        {
            return null;
        }

        var lines = (dto.Lines ?? [])
            .Where(line =>
                !string.IsNullOrWhiteSpace(line.Id) &&
                line.Order is >= 0 &&
                Enum.IsDefined(line.Kind))
            .Select(line => new ChordSheetLine(
                line.Id!,
                line.Order!.Value,
                line.Kind,
                line.Text ?? string.Empty,
                line.StartSeconds is { } start && double.IsFinite(start) && start >= 0d
                    ? start
                    : null,
                line.Confidence is { } confidence && double.IsFinite(confidence)
                    ? confidence
                    : 0d,
                line.SectionLabel))
            .ToArray();
        if (lines.Length == 0 && string.IsNullOrWhiteSpace(dto.RawText))
        {
            return null;
        }
        return ChordSheetDocument.Normalize(new ChordSheetDocument(
            dto.Id,
            dto.Title,
            dto.SourceKind,
            dto.SourceUrl,
            dto.RawText ?? string.Empty,
            dto.UpdatedAtUtc,
            dto.LeadSeconds ?? 2d,
            lines,
            dto.ViewSwitchSeconds,
            dto.ViewSwitchLineId));
    }

    private static bool TryCreatePerformanceSession(
        DrumPerformanceSessionDto dto,
        out DrumPerformanceSession session)
    {
        session = null!;
        if (string.IsNullOrWhiteSpace(dto.Id) || dto.FinishedAtUtc == default ||
            dto.TotalHits is < 0 || dto.AccurateHits is < 0 ||
            dto.EarlyHits is < 0 || dto.LateHits is < 0 ||
            dto.ExpectedHits is < 0 || dto.MissedHits is < 0 || dto.ExtraHits is < 0 ||
            dto.AccurateHits > dto.TotalHits || dto.EarlyHits > dto.TotalHits ||
            dto.LateHits > dto.TotalHits ||
            dto.AccurateHits + dto.EarlyHits + dto.LateHits > dto.TotalHits ||
            dto.MissedHits > dto.ExpectedHits ||
            !double.IsFinite(dto.LatencyCompensationMilliseconds) ||
            !double.IsFinite(dto.AccuracyPercent) ||
            !double.IsFinite(dto.MeanAbsoluteErrorMilliseconds) ||
            !double.IsFinite(dto.MaximumErrorMilliseconds) ||
            dto.AccuracyPercent is < 0d or > 100d ||
            dto.MeanAbsoluteErrorMilliseconds is < 0d ||
            dto.MaximumErrorMilliseconds is < 0d)
        {
            return false;
        }

        session = new DrumPerformanceSession(
            dto.Id,
            dto.FinishedAtUtc,
            dto.FinishedAtNaturalEnd,
            Math.Clamp(dto.LatencyCompensationMilliseconds, -500d, 500d),
            dto.TotalHits,
            dto.AccurateHits,
            dto.EarlyHits,
            dto.LateHits,
            dto.AccuracyPercent,
            dto.MeanAbsoluteErrorMilliseconds,
            dto.MaximumErrorMilliseconds,
            dto.ExpectedHits,
            dto.MissedHits,
            dto.ExtraHits,
            dto.ReferenceVersion);
        return true;
    }

    private static DrumReferenceMap? TryCreateDrumReference(DrumReferenceDto? dto)
    {
        if (dto is null ||
            string.IsNullOrWhiteSpace(dto.Version) ||
            string.IsNullOrWhiteSpace(dto.SourcePath) ||
            dto.AnalyzedAtUtc == default ||
            !double.IsFinite(dto.Confidence))
        {
            return null;
        }
        var normalized = DrumReferenceMap.Normalize(new DrumReferenceMap(
            dto.Version,
            dto.SourcePath,
            dto.AnalyzedAtUtc,
            dto.Confidence,
            dto.HitTimesSeconds ?? []));
        return normalized.HitTimesSeconds.Count == 0 ? null : normalized;
    }

    private static MediaAnalysisDto ToAnalysisDto(MediaAnalysisRecord record) => new()
    {
        MediaKey = record.MediaKey,
        Tempo = record.Tempo is null ? null : ToTempoDto(record.Tempo),
        TempoOrigin = record.TempoOrigin,
        TempoUpdatedAtUtc = record.TempoUpdatedAtUtc,
        SongStructure = record.SongStructure is null
            ? null
            : new SongStructureDto
            {
                AnalyzedAtUtc = record.SongStructure.AnalyzedAtUtc,
                DurationSeconds = record.SongStructure.DurationSeconds,
                Confidence = record.SongStructure.Confidence,
                Sections = record.SongStructure.Sections.Select(section =>
                    new SongSectionDto
                    {
                        Id = section.Id,
                        StartSeconds = section.StartSeconds,
                        EndSeconds = section.EndSeconds,
                        Label = section.Label,
                        Confidence = section.Confidence,
                        Signature = section.Signature
                    }).ToList()
            },
        ChordSheet = record.ChordSheet is null
            ? null
            : new ChordSheetDto
            {
                Id = record.ChordSheet.Id,
                Title = record.ChordSheet.Title,
                SourceKind = record.ChordSheet.SourceKind,
                SourceUrl = record.ChordSheet.SourceUrl,
                RawText = record.ChordSheet.RawText,
                UpdatedAtUtc = record.ChordSheet.UpdatedAtUtc,
                LeadSeconds = record.ChordSheet.LeadSeconds,
                ViewSwitchSeconds = record.ChordSheet.ViewSwitchSeconds,
                ViewSwitchLineId = record.ChordSheet.ViewSwitchLineId,
                Lines = record.ChordSheet.Lines.Select(line => new ChordSheetLineDto
                {
                    Id = line.Id,
                    Order = line.Order,
                    Kind = line.Kind,
                    Text = line.Text,
                    StartSeconds = line.StartSeconds,
                    Confidence = line.Confidence,
                    SectionLabel = line.SectionLabel
                }).ToList()
            },
        DrumReference = record.DrumReference is null
            ? null
            : new DrumReferenceDto
            {
                Version = record.DrumReference.Version,
                SourcePath = record.DrumReference.SourcePath,
                AnalyzedAtUtc = record.DrumReference.AnalyzedAtUtc,
                Confidence = record.DrumReference.Confidence,
                HitTimesSeconds = record.DrumReference.HitTimesSeconds.ToList()
            },
        PerformanceSessions = record.PerformanceSessions.Select(session =>
            new DrumPerformanceSessionDto
            {
                Id = session.Id,
                FinishedAtUtc = session.FinishedAtUtc,
                FinishedAtNaturalEnd = session.FinishedAtNaturalEnd,
                LatencyCompensationMilliseconds = session.LatencyCompensationMilliseconds,
                TotalHits = session.TotalHits,
                AccurateHits = session.AccurateHits,
                EarlyHits = session.EarlyHits,
                LateHits = session.LateHits,
                AccuracyPercent = session.AccuracyPercent,
                MeanAbsoluteErrorMilliseconds = session.MeanAbsoluteErrorMilliseconds,
                MaximumErrorMilliseconds = session.MaximumErrorMilliseconds,
                ExpectedHits = session.ExpectedHits,
                MissedHits = session.MissedHits,
                ExtraHits = session.ExtraHits,
                ReferenceVersion = session.ReferenceVersion
            }).ToList()
    };

    private static bool TryCreatePlaylistItem(PlaylistItemDto dto, out PlaylistItem item)
    {
        item = null!;
        if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Title) ||
            !Enum.IsDefined(dto.Kind))
        {
            return false;
        }

        if (dto.Kind == PlaylistItemKind.LocalTrack && string.IsNullOrWhiteSpace(dto.TrackId))
        {
            return false;
        }
        if (dto.Kind == PlaylistItemKind.YouTube &&
            (string.IsNullOrWhiteSpace(dto.YouTubeVideoId) ||
             string.IsNullOrWhiteSpace(dto.YouTubeUrl)))
        {
            return false;
        }

        item = new PlaylistItem
        {
            Id = dto.Id,
            Kind = dto.Kind,
            TrackId = dto.TrackId,
            YouTubeVideoId = dto.YouTubeVideoId,
            YouTubeUrl = dto.YouTubeUrl,
            Title = dto.Title,
            ThumbnailUrl = dto.ThumbnailUrl,
            Tempo = TryCreateTempo(dto.Tempo)
        };
        return true;
    }

    private static TempoSettings? TryCreateTempo(TempoDto? tempo)
    {
        if (tempo?.Bpm is not (>= 40d and <= 240d) ||
            tempo.FirstBeatSeconds is not >= 0d)
        {
            return null;
        }

        return TempoSettings.Normalize(new TempoSettings(
            tempo.Bpm.Value,
            tempo.FirstBeatSeconds.Value,
            tempo.BeatsPerBar ?? 4,
            tempo.MetronomeEnabled ?? false,
            tempo.MetronomeVolume ?? 0.55d,
            tempo.AnalysisConfidence ?? 0d,
            (tempo.Segments ?? [])
                .Where(segment =>
                    !string.IsNullOrWhiteSpace(segment.Id) &&
                    segment.StartSeconds is >= 0d &&
                    segment.Bpm is >= 40d and <= 240d &&
                    segment.FirstBeatSeconds is >= 0d)
                .Select(segment => new TempoSegment(
                    segment.Id!,
                    segment.StartSeconds!.Value,
                    segment.Bpm!.Value,
                    segment.FirstBeatSeconds!.Value,
                    segment.BeatsPerBar ?? 4,
                    segment.Confidence ?? 0d,
                    segment.SourceName ?? string.Empty,
                    segment.SourceUrl))
                .ToArray()));
    }

    private static TempoDto ToTempoDto(TempoSettings tempo) => new()
    {
        Bpm = tempo.Bpm,
        FirstBeatSeconds = tempo.FirstBeatSeconds,
        BeatsPerBar = tempo.BeatsPerBar,
        MetronomeEnabled = tempo.MetronomeEnabled,
        MetronomeVolume = tempo.MetronomeVolume,
        AnalysisConfidence = tempo.AnalysisConfidence,
        Segments = tempo.EffectiveSegments.Select(segment => new TempoSegmentDto
        {
            Id = segment.Id,
            StartSeconds = segment.StartSeconds,
            Bpm = segment.Bpm,
            FirstBeatSeconds = segment.FirstBeatSeconds,
            BeatsPerBar = segment.BeatsPerBar,
            Confidence = segment.Confidence,
            SourceName = segment.SourceName,
            SourceUrl = segment.SourceUrl
        }).ToList()
    };

    private static AudioEffectSlotSetting? TryCreateAudioEffect(AudioEffectSlotDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) || !Enum.IsDefined(dto.Kind))
        {
            return null;
        }

        Vst3EffectReference? external = null;
        if (dto.Kind == AudioEffectKind.ExternalVst3)
        {
            external = TryCreateVst3EffectReference(dto.ExternalVst3);
            if (external is null)
            {
                return null;
            }
        }

        return AudioEffectSlotSetting.Normalize(new AudioEffectSlotSetting(
            dto.Id,
            dto.Kind,
            dto.IsEnabled ?? true,
            dto.Amount ?? 0.5d,
            dto.Mix ?? 1d,
            external));
    }

    private static AudioEffectSlotDto ToAudioEffectSlotDto(AudioEffectSlotSetting effect) => new()
    {
        Id = effect.Id,
        Kind = effect.Kind,
        IsEnabled = effect.IsEnabled,
        Amount = effect.Amount,
        Mix = effect.Mix,
        ExternalVst3 = effect.ExternalVst3 is null
            ? null
            : ToVst3EffectReferenceDto(effect.ExternalVst3)
    };

    private static Vst3EffectReference? TryCreateVst3EffectReference(
        Vst3EffectReferenceDto? vst)
    {
        if (vst is null ||
            string.IsNullOrWhiteSpace(vst.ModulePath) ||
            string.IsNullOrWhiteSpace(vst.ClassId) ||
            string.IsNullOrWhiteSpace(vst.Name))
        {
            return null;
        }

        try
        {
            var modulePath = Path.GetFullPath(vst.ModulePath);
            return new Vst3EffectReference(
                modulePath,
                string.IsNullOrWhiteSpace(vst.ModuleName)
                    ? Path.GetFileNameWithoutExtension(modulePath)
                    : vst.ModuleName,
                vst.ClassId.Trim(),
                vst.Category ?? "Audio Module Class",
                vst.Name,
                vst.Vendor ?? string.Empty,
                vst.Version ?? string.Empty,
                vst.SdkVersion ?? string.Empty,
                vst.SubCategories ?? string.Empty,
                vst.PresetPath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }
    }

    private static Vst3EffectReferenceDto ToVst3EffectReferenceDto(
        Vst3EffectReference effect) => new()
    {
        ModulePath = effect.ModulePath,
        ModuleName = effect.ModuleName,
        ClassId = effect.ClassId,
        Category = effect.Category,
        Name = effect.Name,
        Vendor = effect.Vendor,
        Version = effect.Version,
        SdkVersion = effect.SdkVersion,
        SubCategories = effect.SubCategories,
        PresetPath = effect.PresetPath
    };

    private static StemSelection NormalizeStemSelection(
        StemSelection? selection,
        int schemaVersion)
    {
        if (selection is null || selection == StemSelection.None ||
            (selection & ~StemSelection.All) != 0)
        {
            return StemSelection.Drumless;
        }

        if (schemaVersion == 1)
        {
            const StemSelection legacyDrumless =
                StemSelection.Bass | StemSelection.Vocals | StemSelection.Other;
            const StemSelection legacyAll = StemSelection.Drums | legacyDrumless;
            if (selection == legacyDrumless)
            {
                return StemSelection.Drumless;
            }
            if (selection == legacyAll)
            {
                return StemSelection.All;
            }
        }

        return selection.Value;
    }

    private static bool IsRecoverableLoadFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        JsonException or
        NotSupportedException;

    private sealed class StudioStateDocument
    {
        public int SchemaVersion { get; set; }
        public string? OutputFolder { get; set; }
        public string? AudioOutputDeviceId { get; set; }
        public string? AudioInputOutputDeviceId { get; set; }
        public int? AudioInputChannelIndex { get; set; }
        public double? AudioInputGain { get; set; }
        public List<AudioInputMonitorDto>? AudioInputMonitors { get; set; }
        public List<AudioEffectBusDto>? AudioEffectBuses { get; set; }
        public List<string>? Vst3EffectFolders { get; set; }
        public List<Vst3EffectReferenceDto>? Vst3EffectCatalog { get; set; }
        public bool? HasScannedVst3Effects { get; set; }
        public Vst3EffectGroupingMode? Vst3EffectGroupingMode { get; set; }
        public string? MidiDeviceName { get; set; }
        public int? MidiDeviceIndex { get; set; }
        public bool? AutoConnectMidi { get; set; }
        public double? MidiVelocitySensitivity { get; set; }
        public string? ActiveLibraryId { get; set; }
        public string? ActiveKitId { get; set; }
        public double? TrackVolume { get; set; }
        public string? VstModulePath { get; set; }
        public string? VstClassId { get; set; }
        public bool? AutoLoadVst { get; set; }
        public StemSelection? StemSelection { get; set; }
        public double? PerformanceLatencyCompensationMs { get; set; }
        public List<TrackDto>? Tracks { get; set; }
        public List<PlaylistDto>? Playlists { get; set; }
        public List<MediaAnalysisDto>? AnalysisRecords { get; set; }
        public string? SelectedPlaylistId { get; set; }
        public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Sequential;
    }

    private sealed class TrackDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Path { get; set; }
        public TrackVariant Variant { get; set; }
        public DateTimeOffset? DateAddedUtc { get; set; }
        public TempoDto? Tempo { get; set; }
    }

    private sealed class TempoDto
    {
        public double? Bpm { get; set; }
        public double? FirstBeatSeconds { get; set; }
        public int? BeatsPerBar { get; set; }
        public bool? MetronomeEnabled { get; set; }
        public double? MetronomeVolume { get; set; }
        public double? AnalysisConfidence { get; set; }
        public List<TempoSegmentDto>? Segments { get; set; }
    }

    private sealed class TempoSegmentDto
    {
        public string? Id { get; set; }
        public double? StartSeconds { get; set; }
        public double? Bpm { get; set; }
        public double? FirstBeatSeconds { get; set; }
        public int? BeatsPerBar { get; set; }
        public double? Confidence { get; set; }
        public string? SourceName { get; set; }
        public string? SourceUrl { get; set; }
    }

    private sealed class PlaylistDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public bool? IsIncludedInMix { get; set; }
        public List<PlaylistItemDto>? Items { get; set; }
        public List<string>? TrackIds { get; set; }
    }

    private sealed class PlaylistItemDto
    {
        public string? Id { get; set; }
        public PlaylistItemKind Kind { get; set; }
        public string? TrackId { get; set; }
        public string? YouTubeVideoId { get; set; }
        public string? YouTubeUrl { get; set; }
        public string? Title { get; set; }
        public string? ThumbnailUrl { get; set; }
        public TempoDto? Tempo { get; set; }
    }

    private sealed class AudioInputMonitorDto
    {
        public int? ChannelIndex { get; set; }
        public double? Gain { get; set; }
        public AudioInputProfileKind? Profile { get; set; }
        public bool? EffectsBypassed { get; set; }
        public List<AudioEffectSlotDto>? Effects { get; set; }
    }

    private sealed class AudioEffectSlotDto
    {
        public string? Id { get; set; }
        public AudioEffectKind Kind { get; set; }
        public bool? IsEnabled { get; set; }
        public double? Amount { get; set; }
        public double? Mix { get; set; }
        public Vst3EffectReferenceDto? ExternalVst3 { get; set; }
    }

    private sealed class AudioEffectBusDto
    {
        public AudioEffectBusTarget Target { get; set; }
        public bool? EffectsBypassed { get; set; }
        public List<AudioEffectSlotDto>? Effects { get; set; }
    }

    private sealed class Vst3EffectReferenceDto
    {
        public string? ModulePath { get; set; }
        public string? ModuleName { get; set; }
        public string? ClassId { get; set; }
        public string? Category { get; set; }
        public string? Name { get; set; }
        public string? Vendor { get; set; }
        public string? Version { get; set; }
        public string? SdkVersion { get; set; }
        public string? SubCategories { get; set; }
        public string? PresetPath { get; set; }
    }

    private sealed class MediaAnalysisDto
    {
        public string? MediaKey { get; set; }
        public TempoDto? Tempo { get; set; }
        public TempoAnalysisOrigin TempoOrigin { get; set; }
        public DateTimeOffset? TempoUpdatedAtUtc { get; set; }
        public SongStructureDto? SongStructure { get; set; }
        public ChordSheetDto? ChordSheet { get; set; }
        public DrumReferenceDto? DrumReference { get; set; }
        public List<DrumPerformanceSessionDto>? PerformanceSessions { get; set; }
    }

    private sealed class SongStructureDto
    {
        public DateTimeOffset AnalyzedAtUtc { get; set; }
        public double? DurationSeconds { get; set; }
        public double? Confidence { get; set; }
        public List<SongSectionDto>? Sections { get; set; }
    }

    private sealed class SongSectionDto
    {
        public string? Id { get; set; }
        public double? StartSeconds { get; set; }
        public double? EndSeconds { get; set; }
        public string? Label { get; set; }
        public double? Confidence { get; set; }
        public string? Signature { get; set; }
    }

    private sealed class ChordSheetDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public ChordSheetSourceKind SourceKind { get; set; }
        public string? SourceUrl { get; set; }
        public string? RawText { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public double? LeadSeconds { get; set; }
        public double? ViewSwitchSeconds { get; set; }
        public string? ViewSwitchLineId { get; set; }
        public List<ChordSheetLineDto>? Lines { get; set; }
    }

    private sealed class ChordSheetLineDto
    {
        public string? Id { get; set; }
        public int? Order { get; set; }
        public ChordSheetLineKind Kind { get; set; }
        public string? Text { get; set; }
        public double? StartSeconds { get; set; }
        public double? Confidence { get; set; }
        public string? SectionLabel { get; set; }
    }

    private sealed class DrumReferenceDto
    {
        public string? Version { get; set; }
        public string? SourcePath { get; set; }
        public DateTimeOffset AnalyzedAtUtc { get; set; }
        public double Confidence { get; set; }
        public List<double>? HitTimesSeconds { get; set; }
    }

    private sealed class DrumPerformanceSessionDto
    {
        public string? Id { get; set; }
        public DateTimeOffset FinishedAtUtc { get; set; }
        public bool FinishedAtNaturalEnd { get; set; }
        public double LatencyCompensationMilliseconds { get; set; }
        public int TotalHits { get; set; }
        public int AccurateHits { get; set; }
        public int EarlyHits { get; set; }
        public int LateHits { get; set; }
        public double AccuracyPercent { get; set; }
        public double MeanAbsoluteErrorMilliseconds { get; set; }
        public double MaximumErrorMilliseconds { get; set; }
        public int ExpectedHits { get; set; }
        public int MissedHits { get; set; }
        public int ExtraHits { get; set; }
        public string? ReferenceVersion { get; set; }
    }
}
