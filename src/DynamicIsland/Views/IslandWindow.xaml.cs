using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;
using DynamicIsland.Settings;
using DynamicIsland.ViewModels;
using Microsoft.Win32;

namespace DynamicIsland.Views;

public partial class IslandWindow : Window
{
    private static readonly Duration ResizeDuration = TimeSpan.FromMilliseconds(480);
    private static readonly Duration FadeOutDuration = TimeSpan.FromMilliseconds(120);
    private static readonly Duration FadeInDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan FadeInDelay = TimeSpan.FromMilliseconds(90);

    public static readonly DependencyProperty IslandRadiusProperty = DependencyProperty.Register(
        nameof(IslandRadius), typeof(double), typeof(IslandWindow),
        new PropertyMetadata(18.0, (d, _) => ((IslandWindow)d).ApplyRadius()));

    private readonly IslandViewModel _vm;
    private readonly VolumeService _volume;
    private readonly DispatcherTimer _hoverExitTimer;
    private readonly DispatcherTimer _topmostTimer;
    private nint _hwnd;
    private FrameworkElement? _currentPanel;
    private bool _equalizerRunning;
    private readonly AppSettings _settings;
    private bool _effectRunning;
    private bool _effectDirty;   // settings/colours changed while running; rebuild on next chance
    private int _effectSwapToken;
    private ImageSource? _paletteCover;
    private (uint[] Colors, byte Alpha)? _coverColors;
    private readonly List<(IAnimatable Target, DependencyProperty Property)> _effectAnimations = [];
    private double _gradientTarget;
    private readonly ScaleTransform _gradientScale = new();
    private bool _suppressed;

    // The window is a layered (per-pixel alpha) window: WPF re-uploads the whole surface every
    // animated frame, so we keep it only as large as the island except while animating.
    private const double MaxWindowWidth = 440;
    private const double MaxWindowHeight = 200;
    private const double EdgeMargin = 8;        // minimum gap between the island and the screen edges
    private const double CompactWidth = 160;
    private const double CompactHeight = 36;
    private readonly double _topOffset;
    private readonly DispatcherTimer _shrinkTimer;
    private Size _targetIslandSize = new(CompactWidth, CompactHeight);

    // Where the user put the island: centre of the collapsed pill, screen DIPs.
    private Point _anchor;
    // The window's rectangle (screen DIPs): where the largest island would sit for this anchor.
    // Every smaller island placed for the same anchor lies inside it, so the window never has to
    // move while the island animates — only its height is trimmed at the bottom when idle.
    private Rect _region;

    private Point? _dragStart;         // cursor (screen DIPs) when the button went down
    private Vector _dragGrabOffset;    // anchor minus cursor while dragging
    private bool _dragging;

    public IslandWindow(IslandViewModel vm, AppSettings settings, VolumeService volume)
    {
        InitializeComponent();
        _vm = vm;
        _volume = volume;
        _settings = settings;
        DataContext = vm;

        _topOffset = Math.Max(0, settings.TopOffset);
        _anchor = AnchorFromSettings();

        _shrinkTimer = new DispatcherTimer { Interval = ResizeDuration.TimeSpan + TimeSpan.FromMilliseconds(100) };
        _shrinkTimer.Tick += (_, _) =>
        {
            _shrinkTimer.Stop();
            UpdateWindowBounds();
        };

        foreach (var panel in Panels)
            panel.RenderTransform = new ScaleTransform(1, 1);
        foreach (var bar in Equalizer.Children.OfType<Rectangle>())
            bar.RenderTransform = new ScaleTransform(1, 0.3);
        ApplyEqualizerColors();
        foreach (var blob in Blobs)
            blob.RenderTransform = new TranslateTransform();
        BlobLayer.RenderTransform = _gradientScale;

        _hoverExitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _hoverExitTimer.Tick += (_, _) =>
        {
            _hoverExitTimer.Stop();
            if (_vm.IsSeeking) return;   // don't collapse under a seek drag; re-armed when it ends
            _vm.IsHovered = false;
        };

        // Other topmost windows (taskbar, some apps) can push us down; re-assert periodically.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) => NativeMethods.EnsureTopmost(_hwnd);

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Island.SizeChanged += (_, _) =>
        {
            ApplyRadius();
            PositionIsland();
            // Stretch the blob space with the island so the motion follows the resize animation.
            _gradientScale.ScaleX = Island.ActualWidth / GradientSpace.Width;
            _gradientScale.ScaleY = Island.ActualHeight / GradientSpace.Height;
        };
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            PositionWindow();
            UpdateVisualState(animate: false);
            _topmostTimer.Start();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _topmostTimer.Stop();
        };
    }

    public double IslandRadius
    {
        get => (double)GetValue(IslandRadiusProperty);
        set => SetValue(IslandRadiusProperty, value);
    }

    private FrameworkElement[] Panels =>
    [
        CompactClockPanel, CompactMediaPanel, ExpandedClockPanel, ExpandedMediaPanel,
        LevelPanel, BatteryPanel, NotificationPanel,
    ];

    /// <summary>Hide the island (e.g. while a fullscreen app is running).</summary>
    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        var fade = new DoubleAnimation(suppressed ? 0 : 1, TimeSpan.FromMilliseconds(200));
        if (suppressed)
            fade.Completed += (_, _) => { if (_suppressed) Visibility = Visibility.Hidden; };
        else
            Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, fade);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeToolWindow(_hwnd);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(PositionWindow);

    private void PositionWindow()
    {
        if (!_dragging)
            _anchor = AnchorFromSettings();
        UpdateWindowBounds();
    }

    /// <summary>Moves the island back to its default spot (centred at the top).</summary>
    public void ResetPosition()
    {
        _settings.PositionX = null;
        _settings.PositionY = null;
        _settings.Save();
        PositionWindow();
    }

    private static Rect WorkArea => SystemParameters.WorkArea;

    // The island may go anywhere on the primary screen, including over the taskbar.
    private static Rect ScreenArea => new(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

    // The top gap honours TopOffset when the user asked for the island to sit closer to the edge.
    private double TopMargin => Math.Min(EdgeMargin, _topOffset);

    private Point AnchorFromSettings()
    {
        var screen = ScreenArea;
        var work = WorkArea;   // the default spot stays clear of a taskbar docked at the top
        var anchor = _settings.PositionX is { } x && _settings.PositionY is { } y
            ? new Point(screen.Left + x * screen.Width, screen.Top + y * screen.Height)
            : new Point(work.Left + work.Width / 2, work.Top + _topOffset + CompactHeight / 2);
        return ClampAnchor(anchor);
    }

    private void SavePosition()
    {
        var area = ScreenArea;
        if (area.Width <= 0 || area.Height <= 0) return;
        _settings.PositionX = Math.Round((_anchor.X - area.Left) / area.Width, 4);
        _settings.PositionY = Math.Round((_anchor.Y - area.Top) / area.Height, 4);
        _settings.Save();
    }

    /// <summary>Keeps the collapsed pill fully on screen.</summary>
    private Point ClampAnchor(Point p)
    {
        var area = ScreenArea;
        return new Point(
            Clamp(p.X, area.Left + EdgeMargin + CompactWidth / 2, area.Right - EdgeMargin - CompactWidth / 2),
            Clamp(p.Y, area.Top + TopMargin + CompactHeight / 2, area.Bottom - EdgeMargin - CompactHeight / 2));
    }

    /// <summary>
    /// Where an island of the given size goes for the current anchor (screen DIPs): centred on the
    /// anchor horizontally; growing downwards in the upper half of the screen and upwards in the
    /// lower half; then pushed back inside the screen so it never crosses an edge.
    /// </summary>
    private Rect PlaceIsland(Size size)
    {
        var area = ScreenArea;
        bool growDown = _anchor.Y < area.Top + area.Height / 2;
        double left = _anchor.X - size.Width / 2;
        double top = growDown
            ? _anchor.Y - CompactHeight / 2
            : _anchor.Y + CompactHeight / 2 - size.Height;
        left = Clamp(left, area.Left + EdgeMargin, area.Right - EdgeMargin - size.Width);
        top = Clamp(top, area.Top + TopMargin, area.Bottom - EdgeMargin - size.Height);
        return new Rect(left, top, size.Width, size.Height);
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Max(min, Math.Min(value, max));

    /// <summary>Places the island inside the window according to its current (animated) size.</summary>
    private void PositionIsland()
    {
        double w = Island.ActualWidth > 0 ? Island.ActualWidth : Island.Width;
        double h = Island.ActualHeight > 0 ? Island.ActualHeight : Island.Height;
        var rect = PlaceIsland(new Size(w, h));
        Canvas.SetLeft(Island, rect.Left - _region.Left);
        Canvas.SetTop(Island, rect.Top - _region.Top);
    }

    // The window's left/top edges stay put for a given anchor: a layered window shows its stale
    // surface at the new position for a frame before WPF re-renders, so moving them while the
    // island animates would make it visibly jump. Only the height is trimmed at the bottom (to save
    // surface uploads) once the animation settles; the full height is kept while it runs.
    private void UpdateWindowBounds()
    {
        _region = PlaceIsland(new Size(MaxWindowWidth, MaxWindowHeight));
        double height = MaxWindowHeight;
        if (!_shrinkTimer.IsEnabled)
        {
            var island = PlaceIsland(_targetIslandSize);
            height = Math.Min(MaxWindowHeight, Math.Ceiling(island.Bottom - _region.Top) + 2);
        }
        SetWindowBounds(_region.Left, _region.Top, _region.Width, height);
        PositionIsland();

        // Over the taskbar (itself topmost) clicking the taskbar raises it above us; re-assert
        // topmost more often there so the island doesn't stay hidden behind it.
        var interval = WorkArea.Contains(PlaceIsland(_targetIslandSize))
            ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(300);
        if (_topmostTimer.Interval != interval)
            _topmostTimer.Interval = interval;
    }

    /// <summary>Moves and resizes the window (DIPs) in one go.</summary>
    private void SetWindowBounds(double left, double top, double width, double height)
    {
        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        if (_hwnd == 0 || toDevice is null)
        {
            Width = width;
            Height = height;
            Left = left;
            Top = top;
            return;
        }

        var m = toDevice.Value;
        var bounds = new Int32Rect(
            (int)Math.Round(left * m.M11), (int)Math.Round(top * m.M22),
            (int)Math.Ceiling(width * m.M11), (int)Math.Ceiling(height * m.M22));
        if (bounds == _lastBounds) return;
        _lastBounds = bounds;
        NativeMethods.SetBounds(_hwnd, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private Int32Rect _lastBounds;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IslandViewModel.Mode):
            case nameof(IslandViewModel.Toast):
            case nameof(IslandViewModel.IsPlaying):
            case nameof(IslandViewModel.ShowCompactMedia):
                UpdateVisualState(animate: true);
                break;
            case nameof(IslandViewModel.HasMedia):
                UpdateVisualState(animate: true);
                UpdateCoverColors();
                break;
            case nameof(IslandViewModel.Cover):
                UpdateCoverColors();
                break;
        }
    }

    private void UpdateVisualState(bool animate)
    {
        var (panel, width, height, radius) = _vm.Mode switch
        {
            IslandMode.Toast => _vm.Toast switch
            {
                ToastKind.Notification => (NotificationPanel, 390.0, 84.0, 30.0),
                ToastKind.Battery => (BatteryPanel, 320.0, 48.0, 24.0),
                _ => ((FrameworkElement)LevelPanel, 320.0, 48.0, 24.0),
            },
            IslandMode.Expanded => _vm.HasMedia
                ? (ExpandedMediaPanel, 400.0, 164.0, 40.0)
                : ((FrameworkElement)ExpandedClockPanel, 320.0, 96.0, 34.0),
            _ => _vm.HasMedia && _vm.ShowCompactMedia
                ? (CompactMediaPanel, 250.0, 36.0, 18.0)
                : ((FrameworkElement)CompactClockPanel, 160.0, 36.0, 18.0),
        };

        AnimateTo(width, height, radius, animate);
        SwitchPanel(panel, animate);
        UpdateEqualizer(panel == CompactMediaPanel && _vm.IsPlaying);
        UpdateGradient(animate);
    }

    private void AnimateTo(double width, double height, double radius, bool animate)
    {
        _targetIslandSize = new Size(width, height);
        _shrinkTimer.Stop();

        if (!animate)
        {
            Island.BeginAnimation(WidthProperty, null);
            Island.BeginAnimation(HeightProperty, null);
            BeginAnimation(IslandRadiusProperty, null);
            Island.Width = width;
            Island.Height = height;
            IslandRadius = radius;
            UpdateWindowBounds();
            return;
        }

        // Give the animation (including overshoot) room, then shrink back once it settles.
        _shrinkTimer.Start();
        UpdateWindowBounds();

        // Slight overshoot gives the "springy" Dynamic Island feel.
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(width, ResizeDuration) { EasingFunction = ease });
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(height, ResizeDuration) { EasingFunction = ease });
        BeginAnimation(IslandRadiusProperty, new DoubleAnimation(radius, ResizeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void ApplyRadius()
    {
        double w = Island.ActualWidth, h = Island.ActualHeight;
        double r = Math.Min(IslandRadius, Math.Min(w, h) / 2);
        Island.CornerRadius = new CornerRadius(r);
        RingLayer.CornerRadius = new CornerRadius(r);
        if (w > 0 && h > 0)
            Island.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    private void SwitchPanel(FrameworkElement next, bool animate)
    {
        if (ReferenceEquals(_currentPanel, next)) return;
        var previous = _currentPanel;
        _currentPanel = next;

        if (previous is not null)
        {
            previous.IsHitTestVisible = false;
            var fadeOut = new DoubleAnimation(0, animate ? FadeOutDuration : TimeSpan.Zero);
            fadeOut.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_currentPanel, previous))
                    previous.Visibility = Visibility.Collapsed;
            };
            previous.BeginAnimation(OpacityProperty, fadeOut);
        }

        next.Visibility = Visibility.Visible;
        next.IsHitTestVisible = true;

        if (!animate)
        {
            next.BeginAnimation(OpacityProperty, null);
            next.Opacity = 1;
            return;
        }

        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeInDuration) { BeginTime = FadeInDelay });
        var scale = (ScaleTransform)next.RenderTransform;
        var grow = new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(380))
        {
            BeginTime = FadeInDelay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void UpdateEqualizer(bool run)
    {
        if (run == _equalizerRunning) return;
        _equalizerRunning = run;

        double[] periods = [0.42, 0.55, 0.36, 0.61, 0.47];
        int i = 0;
        foreach (var bar in Equalizer.Children.OfType<Rectangle>())
        {
            var scale = (ScaleTransform)bar.RenderTransform;
            if (run)
            {
                var bounce = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(periods[i % periods.Length]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                // Every frame re-uploads the layered window; the bars look fine at a low frame rate.
                Timeline.SetDesiredFrameRate(bounce, 20);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
            }
            else
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = 0.3;
            }
            i++;
        }
    }

    /// <summary>Re-reads the gradient settings (palette, effect, compact visibility) and applies them.</summary>
    public void ApplyGradientSettings()
    {
        // Swap to the new palette/effect; when it's being turned off, just let it fade out.
        if (GradientOpacityFor(_vm.Mode) > 0)
            RestartEffectSmoothly();
        UpdateGradient(animate: true);
        // The equalizer may follow the gradient's palette.
        ApplyEqualizerColors();
    }

    /// <summary>Re-reads the equalizer colour setting and repaints the bars.</summary>
    public void ApplyEqualizerColors()
    {
        var bars = Equalizer.Children.OfType<Rectangle>().ToArray();
        var colors = ResolveEqualizerColors();
        if (colors is null)
        {
            var accent = (Brush)FindResource("AccentBrush");
            foreach (var bar in bars)
                bar.Fill = accent;
            return;
        }

        // Spread the palette across the bars, each with a slight vertical sheen towards the next colour.
        for (int i = 0; i < bars.Length; i++)
        {
            double t = bars.Length > 1 ? (double)i / (bars.Length - 1) : 0;
            var bottom = SamplePalette(colors, t);
            var top = SamplePalette(colors, Math.Min(1, t + 0.25));
            var brush = new LinearGradientBrush(top, bottom, 90);
            brush.Freeze();
            bars[i].Fill = brush;
        }
    }

    /// <summary>Palette for the equalizer, or null for the plain accent colour.</summary>
    private uint[]? ResolveEqualizerColors()
    {
        var style = _settings.EqualizerColors switch
        {
            EqualizerStyle.Accent => (GradientStyle?)null,
            EqualizerStyle.Gradient => _settings.Gradient == GradientStyle.Off ? null : _settings.Gradient,
            var s => Enum.Parse<GradientStyle>(s.ToString()),
        };
        return style switch
        {
            null => null,
            GradientStyle.Cover => (_coverColors ?? Palettes[GradientStyle.Aurora]).Colors,
            var s when Palettes.TryGetValue(s.Value, out var palette) => palette.Colors,
            _ => null,
        };
    }

    /// <summary>Linear interpolation across the palette's colours at position t (0..1), fully opaque.</summary>
    private static Color SamplePalette(uint[] colors, double t)
    {
        if (colors.Length == 1) return Rgb(colors[0], 255);
        double pos = t * (colors.Length - 1);
        int i = Math.Min((int)pos, colors.Length - 2);
        double f = pos - i;
        Color a = Rgb(colors[i], 255), b = Rgb(colors[i + 1], 255);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * f),
            (byte)(a.G + (b.G - a.G) * f),
            (byte)(a.B + (b.B - a.B) * f));
    }

    /// <summary>Recomputes the album-art palette when the cover (or media presence) changes.</summary>
    private void UpdateCoverColors()
    {
        var cover = _vm.HasMedia ? _vm.Cover : null;
        if (ReferenceEquals(cover, _paletteCover)) return;
        _paletteCover = cover;
        _coverColors = CoverPalette.Extract(cover as BitmapSource);
        if (_settings.Gradient == GradientStyle.Cover)
            RestartEffectSmoothly();
        ApplyEqualizerColors();
    }

    /// <summary>Album-art colours when that palette is chosen and media is playing, else a preset.</summary>
    private (uint[] Colors, byte Alpha) ResolvePalette() => _settings.Gradient switch
    {
        GradientStyle.Cover => _coverColors ?? Palettes[GradientStyle.Aurora],
        var style when Palettes.TryGetValue(style, out var palette) => palette,
        _ => Palettes[GradientStyle.Aurora],
    };

    /// <summary>Briefly fades the running effect out, rebuilds it with current settings, fades back in.</summary>
    private void RestartEffectSmoothly()
    {
        if (!_effectRunning || _gradientTarget == 0) return; // the next StartEffect picks it up
        _effectDirty = true;
        int token = ++_effectSwapToken;
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fadeOut.Completed += (_, _) =>
        {
            if (token != _effectSwapToken || !_effectDirty || _gradientTarget == 0) return;
            StopEffect();
            StartEffect();
            EffectLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(_gradientTarget, TimeSpan.FromMilliseconds(400)));
        };
        EffectLayer.BeginAnimation(OpacityProperty, fadeOut);
    }

    private IEnumerable<Ellipse> Blobs => BlobLayer.Children.OfType<Ellipse>();

    // Blob coordinate space: the largest island size. The layer is scaled to the actual island.
    private static readonly Size GradientSpace = new(400, 164);

    // Each blob drifts between two points with its own X/Y periods, so the combined motion never
    // visibly repeats. Coordinates are the blob's top-left in GradientSpace.
    private static readonly (double X0, double X1, double Y0, double Y1, double Tx, double Ty)[] BlobPaths =
    [
        (-170, 60, -150, -20, 7.0, 5.3),
        (180, -40, -40, -130, 8.2, 6.1),
        (40, 230, 10, -100, 6.6, 7.7),
    ];

    // Three colours (RGB) and peak alpha per palette.
    private static readonly Dictionary<GradientStyle, (uint[] Colors, byte Alpha)> Palettes = new()
    {
        [GradientStyle.Aurora] = ([0x5E5CE6, 0xFF375F, 0x40C8E0], 0x78),
        [GradientStyle.Sunset] = ([0xFF9F0A, 0xFF375F, 0xBF5AF2], 0x78),
        [GradientStyle.Ocean] = ([0x0A84FF, 0x40C8E0, 0x30D1B0], 0x78),
        [GradientStyle.Forest] = ([0x30D158, 0x00A88F, 0xA8E05F], 0x6C),
        [GradientStyle.Neon] = ([0xFF2D95, 0x00F0FF, 0x7B2DFF], 0x80),
        [GradientStyle.Mono] = ([0xFFFFFF, 0x8E8E93, 0xD1D1D6], 0x38),
    };

    private static Color Rgb(uint rgb, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha, 0, 255), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private double GradientOpacityFor(IslandMode mode) =>
        _settings.Gradient == GradientStyle.Off ? 0
        : mode == IslandMode.Compact ? (_settings.GradientInCompact ? 0.9 : 0)
        : 1;

    private void UpdateGradient(bool animate)
    {
        double target = GradientOpacityFor(_vm.Mode);
        if (target == _gradientTarget && (target == 0 || _effectRunning)) return;
        bool appearing = _gradientTarget == 0 && target > 0;
        _gradientTarget = target;

        _effectSwapToken++; // this fade supersedes any palette swap in progress
        if (target > 0)
        {
            EffectLayer.Visibility = Visibility.Visible;
            if (_effectDirty) StopEffect();
            StartEffect();
        }

        var duration = animate ? TimeSpan.FromMilliseconds(appearing ? 500 : 250) : TimeSpan.Zero;
        var fade = new DoubleAnimation(target, duration)
        {
            BeginTime = appearing && animate ? FadeInDelay : TimeSpan.Zero,
        };
        if (target == 0)
        {
            fade.Completed += (_, _) =>
            {
                if (_gradientTarget > 0) return;
                EffectLayer.Visibility = Visibility.Collapsed;
                StopEffect();
            };
        }
        EffectLayer.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Builds the brushes for the current palette/effect and starts its animations.</summary>
    private void StartEffect()
    {
        if (_effectRunning) return;
        _effectRunning = true;
        _effectDirty = false;

        var (c, alpha) = ResolvePalette();
        var effect = _settings.GradientEffect;

        BlobLayer.Visibility = effect is GradientEffect.Drift or GradientEffect.Pulse
            ? Visibility.Visible : Visibility.Collapsed;
        FillLayer.Background = null;
        RingLayer.BorderBrush = null;
        RingLayer.BorderThickness = new Thickness(0);

        switch (effect)
        {
            case GradientEffect.Drift:
            {
                // Soft blobs wandering around.
                SetBlobFills(c, alpha);
                int i = 0;
                foreach (var blob in Blobs)
                {
                    var t = (TranslateTransform)blob.RenderTransform;
                    var path = BlobPaths[i++ % BlobPaths.Length];
                    Run(t, TranslateTransform.XProperty, Swing(path.X0, path.X1, path.Tx));
                    Run(t, TranslateTransform.YProperty, Swing(path.Y0, path.Y1, path.Ty));
                }
                break;
            }

            case GradientEffect.Pulse:
            {
                // Blobs stay put and "breathe" out of phase with each other.
                SetBlobFills(c, alpha * 1.15);
                double[] periods = [2.2, 2.9, 2.6];
                int i = 0;
                foreach (var blob in Blobs)
                {
                    var t = (TranslateTransform)blob.RenderTransform;
                    var path = BlobPaths[i % BlobPaths.Length];
                    t.X = (path.X0 + path.X1) / 2;
                    t.Y = (path.Y0 + path.Y1) / 2;
                    var breathe = Swing(0.12, 1, periods[i % periods.Length]);
                    breathe.BeginTime = TimeSpan.FromSeconds(-0.8 * i); // start out of phase
                    Run(blob, OpacityProperty, breathe);
                    i++;
                }
                break;
            }

            case GradientEffect.Flow:
            {
                // Colour bands flowing sideways, seamlessly repeating.
                var shift = new TranslateTransform();
                FillLayer.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                    SpreadMethod = GradientSpreadMethod.Repeat,
                    RelativeTransform = shift,
                    GradientStops =
                    {
                        new(Rgb(c[0], alpha), 0), new(Rgb(c[1], alpha), 0.33),
                        new(Rgb(c[2], alpha), 0.66), new(Rgb(c[0], alpha), 1),
                    },
                };
                Run(shift, TranslateTransform.XProperty, Loop(0, 1, 6));
                break;
            }

            case GradientEffect.Spin:
            {
                // The gradient direction slowly rotates around the island's centre.
                var rotate = new RotateTransform(0, 0.5, 0.5);
                FillLayer.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                    RelativeTransform = rotate,
                    GradientStops =
                    {
                        new(Rgb(c[0], alpha), 0), new(Rgb(c[1], alpha * 0.25), 0.5), new(Rgb(c[2], alpha), 1),
                    },
                };
                Run(rotate, RotateTransform.AngleProperty, Loop(0, 360, 9));
                break;
            }

            case GradientEffect.Ring:
            {
                // A bright gradient outline running around the edge, with a faint inner glow.
                var ringRotate = new RotateTransform(0, 0.5, 0.5);
                RingLayer.BorderThickness = new Thickness(2);
                RingLayer.BorderBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                    RelativeTransform = ringRotate,
                    GradientStops =
                    {
                        new(Rgb(c[0], 255), 0), new(Rgb(c[1], 255), 0.5), new(Rgb(c[2], 255), 1),
                    },
                };
                var glowRotate = new RotateTransform(0, 0.5, 0.5);
                FillLayer.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                    RelativeTransform = glowRotate,
                    GradientStops =
                    {
                        new(Rgb(c[0], alpha * 0.35), 0), new(Rgb(c[1], 0), 0.5), new(Rgb(c[2], alpha * 0.35), 1),
                    },
                };
                Run(ringRotate, RotateTransform.AngleProperty, Loop(0, 360, 4));
                Run(glowRotate, RotateTransform.AngleProperty, Loop(0, 360, 4));
                break;
            }

            case GradientEffect.Shimmer:
            {
                // A glossy diagonal highlight sweeping across, then a pause.
                var sweep = new TranslateTransform();
                FillLayer.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0.6),
                    RelativeTransform = sweep,
                    GradientStops =
                    {
                        new(Rgb(c[0], 0), 0.2), new(Rgb(c[0], alpha * 0.8), 0.4), new(Rgb(c[1], alpha * 1.4), 0.5),
                        new(Rgb(c[2], alpha * 0.8), 0.6), new(Rgb(c[2], 0), 0.8),
                    },
                };
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                var keys = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(-1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                keys.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6)), ease));
                keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.8))));
                Run(sweep, TranslateTransform.XProperty, keys);
                break;
            }
        }
    }

    private void StopEffect()
    {
        _effectRunning = false;
        foreach (var (target, property) in _effectAnimations)
            target.BeginAnimation(property, null);
        _effectAnimations.Clear();
        foreach (var blob in Blobs)
        {
            var t = (TranslateTransform)blob.RenderTransform;
            t.X = t.Y = 0;
        }
    }

    private void SetBlobFills(uint[] colors, double alpha)
    {
        int i = 0;
        foreach (var blob in Blobs)
        {
            uint rgb = colors[i++ % colors.Length];
            var brush = new RadialGradientBrush(new GradientStopCollection
            {
                new(Rgb(rgb, alpha), 0),
                new(Rgb(rgb, alpha * 0.38), 0.5),
                new(Rgb(rgb, 0), 1),
            });
            brush.Freeze();
            blob.Fill = brush;
        }
    }

    private void Run(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
    {
        // Slow motion: 30 fps is smooth enough and halves layered-window surface uploads.
        Timeline.SetDesiredFrameRate(animation, 30);
        target.BeginAnimation(property, animation);
        _effectAnimations.Add((target, property));
    }

    /// <summary>Eases back and forth between two values forever.</summary>
    private static DoubleAnimation Swing(double from, double to, double seconds) =>
        new(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

    /// <summary>Runs linearly from one value to another, then jumps back, forever.</summary>
    private static DoubleAnimation Loop(double from, double to, double seconds) =>
        new(from, to, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever };

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverExitTimer.Stop();
        if (!_dragging)
            _vm.IsHovered = true;
    }

    // Dragging the island moves it. It collapses to the pill while dragged; on release the new
    // spot is saved, and expanding later places the bigger island so it stays on screen.
    private void Island_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = CursorPosition(e);

    private void Island_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var cursor = CursorPosition(e);
        if (!_dragging)
        {
            var moved = cursor - start;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _dragging = true;
            // Keep the grab point, but within the pill: the island collapses under the cursor.
            var grab = _anchor - start;
            _dragGrabOffset = new Vector(
                Clamp(grab.X, -(CompactWidth / 2 - 12), CompactWidth / 2 - 12),
                Clamp(grab.Y, -(CompactHeight / 2 - 6), CompactHeight / 2 - 6));
            _hoverExitTimer.Stop();
            _vm.IsHovered = false;
            Island.CaptureMouse();
        }

        _anchor = ClampAnchor(cursor + _dragGrabOffset);
        UpdateWindowBounds();
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            _dragStart = null;
            return;
        }
        e.Handled = true;
        EndDrag();
    }

    private void Island_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _dragStart = null;
        if (!_dragging) return;
        _dragging = false;
        if (Island.IsMouseCaptured)
            Island.ReleaseMouseCapture();   // re-enters via LostMouseCapture, which is now a no-op
        SavePosition();
        if (Island.IsMouseOver)
            _vm.IsHovered = true;
        else
            _hoverExitTimer.Start();
    }

    /// <summary>Cursor position in screen DIPs.</summary>
    private Point CursorPosition(MouseEventArgs e)
    {
        var device = PointToScreen(e.GetPosition(this));
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        return fromDevice is { } m ? m.Transform(device) : device;
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e) => _hoverExitTimer.Start();

    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _volume.Adjust(e.Delta > 0 ? 0.02f : -0.02f);
        e.Handled = true;
    }

    // Click or drag on the progress bar to seek. The events are marked handled so the island's own
    // handlers don't start dragging the window.
    private double SeekFraction(MouseEventArgs e) =>
        SeekBar.ActualWidth > 0 ? e.GetPosition(SeekBar).X / SeekBar.ActualWidth : 0;

    private void SeekBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_vm.CanSeek) return;
        _vm.BeginSeek(SeekFraction(e));
        SeekBar.CaptureMouse();
    }

    private void SeekBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_vm.IsSeeking) return;
        e.Handled = true;
        _vm.UpdateSeek(SeekFraction(e));
    }

    private void SeekBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsSeeking) return;
        e.Handled = true;
        _vm.UpdateSeek(SeekFraction(e));
        SeekBar.ReleaseMouseCapture();   // commits via SeekBar_LostMouseCapture
    }

    private void SeekBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        e.Handled = true;   // keep Island_LostMouseCapture out of it
        if (!_vm.IsSeeking) return;
        _vm.EndSeek(commit: true);   // e.g. capture stolen mid-drag: seek to where it was
        if (!Island.IsMouseOver)
            _hoverExitTimer.Start();
    }

    private void NotificationPanel_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm.DismissToastCommand.CanExecute(null))
            _vm.DismissToastCommand.Execute(null);
    }
}
