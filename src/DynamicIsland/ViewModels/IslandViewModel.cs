using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicIsland.Services;
using DynamicIsland.Settings;

namespace DynamicIsland.ViewModels;

public enum IslandMode { Compact, Expanded, Toast }

public enum ToastKind { None, Volume, Brightness, Battery, Notification }

public partial class IslandViewModel : ObservableObject, IDisposable
{
    // Higher number wins when two toasts compete.
    private const int PriorityBattery = 2;
    private const int PriorityLevel = 3;
    private const int PriorityNotification = 4;

    private const double BatteryInnerWidth = 22;

    private static readonly Brush BatteryNormal = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)));
    private static readonly Brush BatteryCharging = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    private static readonly Brush BatteryLow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));

    private readonly MediaService _media;
    private readonly VolumeService _volume;
    private readonly BrightnessService _brightness;
    private readonly BatteryService _battery;
    private readonly NotificationService _notifications;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _mediaGraceTimer;

    // How long media may be "gone" or paused before the island collapses (covers track switches).
    private static readonly TimeSpan MediaGracePeriod = TimeSpan.FromSeconds(2);

    private int _toastPriority;
    private bool _pendingMediaLoss;
    private bool _pendingCoverClear;
    private byte[]? _lastThumbnail;
    private TimeSpan _mediaPosition;
    private TimeSpan _mediaDuration;
    private DateTimeOffset _mediaUpdated;
    private bool _batteryInitialized;

    public IslandViewModel(
        MediaService media,
        VolumeService volume,
        BrightnessService brightness,
        BatteryService battery,
        NotificationService notifications,
        AppSettings settings)
    {
        _media = media;
        _volume = volume;
        _brightness = brightness;
        _battery = battery;
        _notifications = notifications;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => { UpdateClock(); UpdateProgress(); };

        _toastTimer = new DispatcherTimer();
        _toastTimer.Tick += (_, _) => HideToast();

        _mediaGraceTimer = new DispatcherTimer { Interval = MediaGracePeriod };
        _mediaGraceTimer.Tick += (_, _) => OnMediaGraceElapsed();

        UpdateClock();
    }

    // ---------- State ----------

    [ObservableProperty] private IslandMode _mode;
    [ObservableProperty] private ToastKind _toast;
    [ObservableProperty] private bool _isHovered;

    partial void OnToastChanged(ToastKind value) => UpdateMode();
    partial void OnIsHoveredChanged(bool value) => UpdateMode();

    private void UpdateMode() =>
        Mode = Toast != ToastKind.None ? IslandMode.Toast
             : IsHovered ? IslandMode.Expanded
             : IslandMode.Compact;

    // ---------- Clock ----------

    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _dateText = "";

    private void UpdateClock()
    {
        var now = DateTime.Now;
        TimeText = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        var date = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
        DateText = date.Length > 0 ? char.ToUpper(date[0], CultureInfo.CurrentCulture) + date[1..] : date;
    }

    // ---------- Media ----------

    [ObservableProperty] private bool _hasMedia;
    [ObservableProperty] private string _mediaTitle = "";
    [ObservableProperty] private string _mediaArtist = "";
    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayPauseGlyph))] private bool _isPlaying;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private string _durationText = "";

    public string PlayPauseGlyph => IsPlaying ? "" : "";

    [RelayCommand] private Task PlayPause() => _media.TogglePlayPauseAsync();
    [RelayCommand] private Task Next() => _media.NextAsync();
    [RelayCommand] private Task Previous() => _media.PreviousAsync();

    // ---------- Seeking ----------

    /// <summary>True while the user drags the progress bar; the bar then shows <see cref="_seekFraction"/>.</summary>
    [ObservableProperty] private bool _isSeeking;
    private double _seekFraction;

    public bool CanSeek => HasMedia && _mediaDuration > TimeSpan.Zero;

    public void BeginSeek(double fraction)
    {
        if (!CanSeek) return;
        IsSeeking = true;
        UpdateSeek(fraction);
    }

    public void UpdateSeek(double fraction)
    {
        if (!IsSeeking) return;
        _seekFraction = Math.Clamp(fraction, 0, 1);
        UpdateProgress();
    }

    public void EndSeek(bool commit)
    {
        if (!IsSeeking) return;
        IsSeeking = false;
        if (commit && CanSeek)
        {
            var target = TimeSpan.FromTicks((long)(_mediaDuration.Ticks * _seekFraction));
            // Show the new position right away; the player confirms it via a timeline update.
            _mediaPosition = target;
            _mediaUpdated = DateTimeOffset.Now;
            _ = _media.SeekAsync(target);
        }
        UpdateProgress();
    }

    /// <summary>
    /// Whether the compact island should show the media panel. Unlike <c>HasMedia &amp;&amp; IsPlaying</c>,
    /// it turns off only after a grace period, so the brief gap while a player switches tracks
    /// (session/title momentarily empty, status momentarily paused) doesn't collapse the island.
    /// </summary>
    [ObservableProperty] private bool _showCompactMedia;

    private void ApplyMedia(MediaInfo? info)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.Title))
        {
            if (HasMedia)
            {
                // Possibly transient (track switch): keep showing the old media for a moment.
                _pendingMediaLoss = true;
                _pendingCoverClear |= info is null;
                RestartMediaGrace();
                return;
            }

            ClearMedia(info is null);
            UpdateCompactMedia();
            return;
        }

        _pendingMediaLoss = false;
        _pendingCoverClear = false;
        MediaTitle = info.Title;
        MediaArtist = info.Artist;
        IsPlaying = info.IsPlaying;
        HasMedia = true;
        UpdateCompactMedia();
        _mediaPosition = info.Position;
        _mediaDuration = info.Duration;
        _mediaUpdated = info.LastUpdated;

        if (!ReferenceEquals(info.Thumbnail, _lastThumbnail))
        {
            _lastThumbnail = info.Thumbnail;
            Cover = LoadImage(info.Thumbnail);
        }
        UpdateProgress();
    }

    private void ClearMedia(bool clearCover)
    {
        HasMedia = false;
        IsPlaying = false;
        if (clearCover)
        {
            Cover = null;
            _lastThumbnail = null;
        }
    }

    private void UpdateCompactMedia()
    {
        if (HasMedia && IsPlaying)
        {
            _mediaGraceTimer.Stop();
            ShowCompactMedia = true;
        }
        else if (ShowCompactMedia)
        {
            RestartMediaGrace();
        }
    }

    private void RestartMediaGrace()
    {
        _mediaGraceTimer.Stop();
        _mediaGraceTimer.Start();
    }

    private void OnMediaGraceElapsed()
    {
        _mediaGraceTimer.Stop();
        if (_pendingMediaLoss)
        {
            ClearMedia(_pendingCoverClear);
            _pendingMediaLoss = false;
            _pendingCoverClear = false;
            UpdateProgress();
        }
        ShowCompactMedia = HasMedia && IsPlaying;
    }

    private void UpdateProgress()
    {
        if (!HasMedia || _mediaDuration <= TimeSpan.Zero)
        {
            Progress = 0;
            PositionText = DurationText = "";
            return;
        }

        var position = _mediaPosition;
        if (IsSeeking)
            position = TimeSpan.FromTicks((long)(_mediaDuration.Ticks * _seekFraction));
        // Players update the timeline sparsely; interpolate while playing.
        else if (IsPlaying && _mediaUpdated.Year > 2000)
            position += DateTimeOffset.Now - _mediaUpdated;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > _mediaDuration) position = _mediaDuration;

        Progress = position.TotalSeconds / _mediaDuration.TotalSeconds;
        PositionText = FormatTime(position);
        DurationText = "-" + FormatTime(_mediaDuration - position);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static ImageSource? LoadImage(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return null;
        }
    }

    // ---------- Volume / brightness ----------

    [ObservableProperty] private double _levelValue;
    [ObservableProperty] private string _levelGlyph = "";
    [ObservableProperty] private string _levelText = "";

    private void OnVolume(float volume, bool muted)
    {
        LevelValue = muted ? 0 : volume;
        LevelGlyph = muted || volume <= 0.001f ? ""
                   : volume < 0.34f ? ""
                   : volume < 0.67f ? ""
                   : "";
        LevelText = muted ? "Без звука" : $"{Math.Round(volume * 100)}";
        ShowToast(ToastKind.Volume, PriorityLevel, TimeSpan.FromSeconds(1.8));
    }

    private void OnBrightness(int percent)
    {
        LevelValue = percent / 100.0;
        LevelGlyph = "";
        LevelText = $"{percent}";
        ShowToast(ToastKind.Brightness, PriorityLevel, TimeSpan.FromSeconds(1.8));
    }

    // ---------- Battery ----------

    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private int _batteryPercent;
    [ObservableProperty] private bool _isCharging;
    [ObservableProperty] private double _batteryFillWidth;
    [ObservableProperty] private Brush _batteryBrush = BatteryNormal;
    [ObservableProperty] private string _batteryMessage = "";

    private void OnBattery(BatteryState state)
    {
        bool wasCharging = IsCharging;
        int oldPercent = BatteryPercent;

        HasBattery = state.HasBattery;
        BatteryPercent = Math.Clamp(state.Percent, 0, 100);
        IsCharging = state.IsPluggedIn;
        BatteryFillWidth = Math.Max(1.5, BatteryInnerWidth * BatteryPercent / 100.0);
        BatteryBrush = IsCharging ? BatteryCharging : BatteryPercent <= 20 ? BatteryLow : BatteryNormal;

        if (!_batteryInitialized)
        {
            _batteryInitialized = true;
            return;
        }
        if (!HasBattery) return;

        if (IsCharging != wasCharging)
        {
            BatteryMessage = IsCharging ? "Зарядка" : "Работа от батареи";
            ShowToast(ToastKind.Battery, PriorityBattery, TimeSpan.FromSeconds(3));
        }
        else if (!IsCharging && ((oldPercent > 20 && BatteryPercent <= 20) || (oldPercent > 10 && BatteryPercent <= 10)))
        {
            BatteryMessage = "Низкий заряд";
            ShowToast(ToastKind.Battery, PriorityBattery, TimeSpan.FromSeconds(5));
        }
        else if (IsCharging && oldPercent < 100 && BatteryPercent == 100)
        {
            BatteryMessage = "Полностью заряжено";
            ShowToast(ToastKind.Battery, PriorityBattery, TimeSpan.FromSeconds(3));
        }
    }

    // ---------- Notifications ----------

    [ObservableProperty] private string _notificationApp = "";
    [ObservableProperty] private string _notificationTitle = "";
    [ObservableProperty] private string _notificationBody = "";

    private void OnNotification(NotificationInfo info)
    {
        NotificationApp = info.AppName;
        NotificationTitle = info.Title;
        NotificationBody = info.Body;
        ShowToast(ToastKind.Notification, PriorityNotification, TimeSpan.FromSeconds(5));
    }

    // ---------- Toasts ----------

    private void ShowToast(ToastKind kind, int priority, TimeSpan duration)
    {
        if (Toast != ToastKind.None && priority < _toastPriority) return;
        _toastPriority = priority;
        Toast = kind;
        _toastTimer.Stop();
        _toastTimer.Interval = duration;
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _toastPriority = 0;
        Toast = ToastKind.None;
    }

    /// <summary>Dismiss the current toast (e.g. on click).</summary>
    [RelayCommand] private void DismissToast() => HideToast();

    // ---------- Lifecycle ----------

    public async Task StartAsync()
    {
        _clockTimer.Start();

        if (_settings.EnableBattery)
        {
            _battery.Changed += s => Post(() => OnBattery(s));
            OnBattery(_battery.Current);
            _battery.Start();
        }

        if (_settings.EnableVolume)
        {
            _volume.VolumeChanged += (v, m) => Post(() => OnVolume(v, m));
            _volume.Start();
        }

        if (_settings.EnableBrightness)
        {
            _brightness.BrightnessChanged += b => Post(() => OnBrightness(b));
            _brightness.Start();
        }

        if (_settings.EnableMedia)
        {
            _media.Changed += info => Post(() => ApplyMedia(info));
            try { await _media.StartAsync(); }
            catch (Exception ex) { Log.Write(ex); }
        }

        if (_settings.EnableNotifications)
        {
            _notifications.NotificationReceived += n => Post(() => OnNotification(n));
            await _notifications.StartAsync();
        }
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _toastTimer.Stop();
        _mediaGraceTimer.Stop();
    }
}
