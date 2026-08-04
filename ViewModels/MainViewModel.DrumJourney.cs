using DrumPracticeStudio.Midi;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    private readonly DrumJourneyTempoTracker _drumJourneyTracker = new();
    private readonly object _drumJourneyGate = new();
    private bool _drumJourneyAttached;
    private string? _drumJourneyMediaKey;
    private double _drumJourneyLastPosition = -1d;

    public event EventHandler<DrumJourneyHitEvent>? DrumJourneyHitProduced;
    public event EventHandler<DrumJourneyState>? DrumJourneyStateChanged;

    public void AttachDrumJourney()
    {
        if (_drumJourneyAttached)
        {
            return;
        }

        _drumJourneyAttached = true;
        _midi.NoteReceived += OnDrumJourneyMidiNoteReceived;
        _transportTimer.Tick += OnDrumJourneyTransportTick;
        PublishDrumJourneyState();
    }

    public void DetachDrumJourney()
    {
        if (!_drumJourneyAttached)
        {
            return;
        }

        _drumJourneyAttached = false;
        _midi.NoteReceived -= OnDrumJourneyMidiNoteReceived;
        _transportTimer.Tick -= OnDrumJourneyTransportTick;
    }

    private void OnDrumJourneyMidiNoteReceived(object? sender, MidiNoteMessage message)
    {
        var position = ResolveDrumJourneyPosition();
        var isPlaying = ResolveDrumJourneyIsPlaying();
        var tempo = ResolveDrumJourneyTempo();
        var compensatedPosition = Math.Max(
            0d,
            position - Math.Clamp(
                PerformanceLatencyCompensationMs + _audio.AudioInputEffectLatencyMilliseconds,
                -500d,
                500d) / 1_000d);
        DrumJourneyEvaluation evaluation;
        lock (_drumJourneyGate)
        {
            evaluation = _drumJourneyTracker.RegisterHit(
                compensatedPosition,
                tempo,
                isPlaying);
        }

        var adjustedVelocity = MidiVelocityCurve.Apply(
            message.Velocity,
            Volatile.Read(ref _midiVelocitySensitivity));
        _midiProfile.TryResolve(message.Note, out var articulation);
        var instrument = ResolveDrumJourneyInstrument(message.Note, articulation);
        DrumJourneyHitProduced?.Invoke(
            this,
            new DrumJourneyHitEvent(
                compensatedPosition,
                message.Note,
                adjustedVelocity,
                instrument,
                evaluation.Judgement,
                evaluation.ErrorMilliseconds,
                evaluation.Score,
                evaluation.Streak,
                evaluation.Multiplier,
                evaluation.TempoLocked));
    }

    private void OnDrumJourneyTransportTick(object? sender, EventArgs eventArgs) =>
        PublishDrumJourneyState();

    private void PublishDrumJourneyState()
    {
        if (!_drumJourneyAttached)
        {
            return;
        }

        var mediaKey = ResolveDrumJourneyMediaKey();
        var position = ResolveDrumJourneyPosition();
        var isPlaying = ResolveDrumJourneyIsPlaying();
        var tempo = ResolveDrumJourneyTempo();
        DrumJourneyState state;
        lock (_drumJourneyGate)
        {
            if (!string.Equals(mediaKey, _drumJourneyMediaKey, StringComparison.Ordinal))
            {
                _drumJourneyMediaKey = mediaKey;
                _drumJourneyLastPosition = position;
                _drumJourneyTracker.Reset(clearScore: true);
            }
            else if (_drumJourneyLastPosition >= 0d &&
                     isPlaying &&
                     Math.Abs(position - _drumJourneyLastPosition) > 1.5d)
            {
                // Un salto grande significa que el usuario ha movido el transporte.
                // La fase detectada ya no es válida y se inicia una sesión visual nueva.
                _drumJourneyTracker.Reset(clearScore: true);
            }

            _drumJourneyLastPosition = position;
            state = _drumJourneyTracker.CreateState(position, isPlaying, tempo);
        }

        DrumJourneyStateChanged?.Invoke(this, state);
    }

    private double ResolveDrumJourneyPosition()
    {
        if (CurrentTrack is not null)
        {
            return Math.Max(0d, _audio.TrackPosition.TotalSeconds);
        }

        return _currentYouTubeItem is not null
            ? Math.Max(0d, _youtubePerformancePositionSeconds)
            : 0d;
    }

    private bool ResolveDrumJourneyIsPlaying() => CurrentTrack is not null
        ? _desiredTrackPlaying
        : _currentYouTubeItem is not null && _isYouTubeAudioActive;

    private TempoSettings? ResolveDrumJourneyTempo() =>
        CurrentTrack?.Tempo ?? _currentYouTubeItem?.Tempo;

    private string? ResolveDrumJourneyMediaKey() => CurrentTrack is not null
        ? $"local:{CurrentTrack.Id}"
        : _currentYouTubeItem?.MediaKey;

    private static DrumJourneyInstrument ResolveDrumJourneyInstrument(
        int midiNote,
        string? articulation)
    {
        var normalized = articulation?.ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("kick", StringComparison.Ordinal) || midiNote is 35 or 36)
        {
            return DrumJourneyInstrument.Kick;
        }
        if (normalized.Contains("snare", StringComparison.Ordinal) || midiNote is 38 or 40)
        {
            return DrumJourneyInstrument.Snare;
        }
        if (normalized.Contains("hihat", StringComparison.Ordinal) || midiNote is 42 or 44 or 46)
        {
            return DrumJourneyInstrument.HiHat;
        }
        if (normalized.Contains("tom", StringComparison.Ordinal) ||
            midiNote is 41 or 43 or 45 or 47 or 48 or 50)
        {
            return DrumJourneyInstrument.Tom;
        }
        if (normalized.Contains("crash", StringComparison.Ordinal) ||
            normalized.Contains("ride", StringComparison.Ordinal) ||
            normalized.Contains("cymbal", StringComparison.Ordinal) ||
            midiNote is 49 or 51 or 52 or 53 or 55 or 57 or 59)
        {
            return DrumJourneyInstrument.Cymbal;
        }
        return DrumJourneyInstrument.Other;
    }
}
