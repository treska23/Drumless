using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class PlaybackNavigator
{
    private readonly Random _random;
    private readonly List<string> _queue = [];
    private readonly Stack<string> _history = [];
    private readonly List<string> _shuffleRemaining = [];
    private PlaybackMode _mode = PlaybackMode.Sequential;

    public PlaybackNavigator(Random? random = null) => _random = random ?? Random.Shared;

    public IReadOnlyList<string> Queue => _queue;
    public string? CurrentTrackId { get; private set; }

    public PlaybackMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            _history.Clear();
            ResetShuffleCycle();
        }
    }

    public void SetQueue(IEnumerable<string> trackIds, string? currentTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(trackIds);

        _queue.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trackId in trackIds)
        {
            if (!string.IsNullOrWhiteSpace(trackId) && seen.Add(trackId))
            {
                _queue.Add(trackId);
            }
        }

        CurrentTrackId = FindCanonicalId(currentTrackId);
        _history.Clear();
        ResetShuffleCycle();
    }

    public bool Select(string trackId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        var selected = FindCanonicalId(trackId);
        if (selected is null)
        {
            return false;
        }

        if (string.Equals(CurrentTrackId, selected, StringComparison.Ordinal))
        {
            return true;
        }

        if (CurrentTrackId is not null)
        {
            _history.Push(CurrentTrackId);
        }

        CurrentTrackId = selected;
        RemoveFromShuffleRemaining(selected);
        return true;
    }

    public string? Next(bool automatic = false)
    {
        if (automatic && Mode == PlaybackMode.Single)
        {
            return null;
        }

        return Mode == PlaybackMode.Shuffle
            ? MoveShuffle()
            : MoveSequential();
    }

    public string? NextManual() => Next(automatic: false);

    public string? NextAutomatic() => Next(automatic: true);

    public string? Previous()
    {
        while (_history.TryPop(out var previous))
        {
            var canonical = FindCanonicalId(previous);
            if (canonical is null)
            {
                continue;
            }

            CurrentTrackId = canonical;
            RemoveFromShuffleRemaining(canonical);
            return canonical;
        }

        if (Mode == PlaybackMode.Shuffle || CurrentTrackId is null)
        {
            return null;
        }

        var index = IndexOf(CurrentTrackId);
        if (index <= 0)
        {
            return null;
        }

        CurrentTrackId = _queue[index - 1];
        return CurrentTrackId;
    }

    public void ResetShuffleCycle()
    {
        _shuffleRemaining.Clear();
        foreach (var trackId in _queue)
        {
            if (!string.Equals(trackId, CurrentTrackId, StringComparison.Ordinal))
            {
                _shuffleRemaining.Add(trackId);
            }
        }
    }

    private string? MoveSequential()
    {
        if (_queue.Count == 0)
        {
            return null;
        }

        if (CurrentTrackId is null)
        {
            return MoveTo(_queue[0]);
        }

        var index = IndexOf(CurrentTrackId);
        if (index < 0)
        {
            return MoveTo(_queue[0]);
        }

        return index >= _queue.Count - 1 ? null : MoveTo(_queue[index + 1]);
    }

    private string? MoveShuffle()
    {
        if (_shuffleRemaining.Count == 0)
        {
            return null;
        }

        var index = _random.Next(_shuffleRemaining.Count);
        var selected = _shuffleRemaining[index];
        _shuffleRemaining.RemoveAt(index);
        return MoveTo(selected);
    }

    private string MoveTo(string trackId)
    {
        if (CurrentTrackId is not null &&
            !string.Equals(CurrentTrackId, trackId, StringComparison.Ordinal))
        {
            _history.Push(CurrentTrackId);
        }

        CurrentTrackId = trackId;
        RemoveFromShuffleRemaining(trackId);
        return trackId;
    }

    private string? FindCanonicalId(string? trackId)
    {
        if (trackId is null)
        {
            return null;
        }

        var index = IndexOf(trackId);
        return index < 0 ? null : _queue[index];
    }

    private int IndexOf(string trackId)
    {
        for (var index = 0; index < _queue.Count; index++)
        {
            if (string.Equals(_queue[index], trackId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private void RemoveFromShuffleRemaining(string trackId)
    {
        for (var index = _shuffleRemaining.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_shuffleRemaining[index], trackId, StringComparison.Ordinal))
            {
                _shuffleRemaining.RemoveAt(index);
            }
        }
    }
}
