using System.IO;
using DynamicIsland.Settings;
using Windows.Media.Control;

namespace DynamicIsland.Services;

public sealed record MediaInfo(
    string Title,
    string Artist,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset LastUpdated,
    byte[]? Thumbnail);

/// <summary>
/// Tracks the current system media session (Spotify, browsers, Groove, etc.)
/// via the Global System Media Transport Controls API.
/// Events are raised on background threads.
/// </summary>
public sealed class MediaService : IDisposable
{
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private string _title = "";
    private string _artist = "";
    private byte[]? _thumbnail;
    private bool _isPlaying;
    private TimeSpan _position;
    private TimeSpan _duration;
    private TimeSpan _startTime;
    private DateTimeOffset _lastUpdated;
    private int _propertiesVersion;

    public event Action<MediaInfo?>? Changed;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        await AttachAsync(_manager.GetCurrentSession());
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => _ = AttachAsync(sender.GetCurrentSession());

    private async Task AttachAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        try
        {
            lock (_gate)
            {
                Detach();
                _session = session;
                if (session is null)
                {
                    _title = _artist = "";
                    _thumbnail = null;
                    _isPlaying = false;
                }
                else
                {
                    session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                }
            }

            if (session is null)
            {
                Changed?.Invoke(null);
                return;
            }

            RefreshPlayback(session);
            RefreshTimeline(session);
            await RefreshPropertiesAsync(session);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void Detach()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = RefreshPropertiesAsync(sender);

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        RefreshPlayback(sender);
        Publish();
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        RefreshTimeline(sender);
        Publish();
    }

    private async Task RefreshPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            int version = Interlocked.Increment(ref _propertiesVersion);
            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null) return;

            byte[]? thumbnail = null;
            if (props.Thumbnail is not null)
            {
                try
                {
                    using var winStream = await props.Thumbnail.OpenReadAsync();
                    using var stream = winStream.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    thumbnail = ms.ToArray();
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                }
            }

            lock (_gate)
            {
                // Ignore stale results from an older request (e.g. a session that's no longer current).
                if (version != _propertiesVersion || _session is null) return;
                _title = props.Title ?? "";
                _artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
                _thumbnail = thumbnail;
            }
            Publish();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void RefreshPlayback(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var info = session.GetPlaybackInfo();
            lock (_gate)
                _isPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void RefreshTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var t = session.GetTimelineProperties();
            if (t is null) return;
            lock (_gate)
            {
                _position = t.Position;
                _startTime = t.StartTime;
                _duration = t.EndTime - t.StartTime;
                _lastUpdated = t.LastUpdatedTime;
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void Publish()
    {
        MediaInfo? info;
        lock (_gate)
        {
            info = _session is null
                ? null
                : new MediaInfo(_title, _artist, _isPlaying, _position, _duration, _lastUpdated, _thumbnail);
        }
        Changed?.Invoke(info);
    }

    public Task TogglePlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync() => Run(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => Run(s => s.TrySkipPreviousAsync().AsTask());

    /// <summary>Seeks to <paramref name="position"/>, measured from the start of the track.</summary>
    public Task SeekAsync(TimeSpan position)
    {
        TimeSpan start;
        lock (_gate) start = _startTime;
        return Run(s => s.TryChangePlaybackPositionAsync((start + position).Ticks).AsTask());
    }

    private async Task Run(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate) session = _session;
        if (session is null) return;
        try
        {
            await action(session);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Detach();
            if (_manager is not null)
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
        }
    }
}
