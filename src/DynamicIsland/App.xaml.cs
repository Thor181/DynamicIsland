using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DynamicIsland.Services;
using DynamicIsland.Settings;
using DynamicIsland.ViewModels;
using DynamicIsland.Views;
using H.NotifyIcon;

namespace DynamicIsland;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppSettings _settings = new();
    private MediaService? _media;
    private VolumeService? _volume;
    private BrightnessService? _brightness;
    private BatteryService? _battery;
    private NotificationService? _notifications;
    private FullscreenService? _fullscreen;
    private IslandViewModel? _viewModel;
    private IslandWindow? _window;
    private TaskbarIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\DynamicIsland.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // The island is a small per-pixel-alpha (layered) window. With GPU rendering WPF reads the
        // surface back every frame, which here cost ~3x the memory and kept growing; software
        // rendering is lighter and stable for a window this size.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write(args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write(args.Exception);
            args.SetObserved();
        };

        _settings = AppSettings.Load();
        if (!File.Exists(AppSettings.FilePath))
            _settings.Save();

        _media = new MediaService();
        _volume = new VolumeService(Dispatcher);
        _brightness = new BrightnessService();
        _battery = new BatteryService();
        _notifications = new NotificationService();

        _viewModel = new IslandViewModel(_media, _volume, _brightness, _battery, _notifications, _settings);
        _window = new IslandWindow(_viewModel, _settings, _volume);
        _window.Show();

        CreateTrayIcon();

        _fullscreen = new FullscreenService();
        _fullscreen.Changed += _ => ApplyFullscreenState();
        _fullscreen.Start();

        await _viewModel.StartAsync();
    }

    private void ApplyFullscreenState() =>
        _window?.SetSuppressed(_settings.HideInFullscreen && _fullscreen?.IsFullscreen == true);

    private void CreateTrayIcon()
    {
        var autoStart = new MenuItem { Header = "Запускать вместе с Windows", IsCheckable = true, IsChecked = AutoStart.IsEnabled };
        autoStart.Click += (_, _) => AutoStart.SetEnabled(autoStart.IsChecked);

        var hideFullscreen = new MenuItem { Header = "Скрывать в полноэкранном режиме", IsCheckable = true, IsChecked = _settings.HideInFullscreen };
        hideFullscreen.Click += (_, _) =>
        {
            _settings.HideInFullscreen = hideFullscreen.IsChecked;
            _settings.Save();
            ApplyFullscreenState();
        };

        var gradientMenu = CreateGradientMenu();
        var equalizerMenu = CreateRadioMenu("Цвет эквалайзера", EqualizerNames,
            () => _settings.EqualizerColors, v => _settings.EqualizerColors = v,
            () => _window?.ApplyEqualizerColors());

        var resetPosition = new MenuItem { Header = "Вернуть остров в центр" };
        resetPosition.Click += (_, _) => _window?.ResetPosition();

        var openSettings = new MenuItem { Header = "Открыть файл настроек…" };
        openSettings.Click += (_, _) =>
        {
            _settings.Save();
            Process.Start(new ProcessStartInfo(AppSettings.FilePath) { UseShellExecute = true });
        };

        var exit = new MenuItem { Header = "Выход" };
        exit.Click += (_, _) => Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(autoStart);
        menu.Items.Add(hideFullscreen);
        menu.Items.Add(gradientMenu);
        menu.Items.Add(equalizerMenu);
        menu.Items.Add(resetPosition);
        menu.Items.Add(new Separator());
        menu.Items.Add(openSettings);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Dynamic Island",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = menu,
        };
        // Don't let the tray library put the process into EcoQoS — it would make animations stutter.
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private static readonly (GradientStyle Value, string Name)[] GradientNames =
    [
        (GradientStyle.Off, "Выключен"),
        (GradientStyle.Cover, "Из обложки трека"),
        (GradientStyle.Aurora, "Северное сияние"),
        (GradientStyle.Sunset, "Закат"),
        (GradientStyle.Ocean, "Океан"),
        (GradientStyle.Forest, "Лес"),
        (GradientStyle.Neon, "Неон"),
        (GradientStyle.Mono, "Монохром"),
    ];

    private static readonly (GradientEffect Value, string Name)[] EffectNames =
    [
        (GradientEffect.Drift, "Плавающие пятна"),
        (GradientEffect.Pulse, "Дыхание"),
        (GradientEffect.Flow, "Перелив"),
        (GradientEffect.Spin, "Вращение"),
        (GradientEffect.Ring, "Светящийся контур"),
        (GradientEffect.Shimmer, "Блик"),
    ];

    private static readonly (EqualizerStyle Value, string Name)[] EqualizerNames =
    [
        (EqualizerStyle.Accent, "Стандартный"),
        (EqualizerStyle.Gradient, "Как у градиента"),
        (EqualizerStyle.Cover, "Из обложки трека"),
        (EqualizerStyle.Aurora, "Северное сияние"),
        (EqualizerStyle.Sunset, "Закат"),
        (EqualizerStyle.Ocean, "Океан"),
        (EqualizerStyle.Forest, "Лес"),
        (EqualizerStyle.Neon, "Неон"),
        (EqualizerStyle.Mono, "Монохром"),
    ];

    private MenuItem CreateGradientMenu()
    {
        var root = new MenuItem { Header = "Градиент" };
        root.Items.Add(CreateRadioMenu("Цвета", GradientNames, () => _settings.Gradient, v => _settings.Gradient = v));
        root.Items.Add(CreateRadioMenu("Эффект", EffectNames, () => _settings.GradientEffect, v => _settings.GradientEffect = v));

        var inCompact = new MenuItem { Header = "Показывать в свёрнутом режиме", IsCheckable = true, IsChecked = _settings.GradientInCompact };
        inCompact.Click += (_, _) =>
        {
            _settings.GradientInCompact = inCompact.IsChecked;
            _settings.Save();
            _window?.ApplyGradientSettings();
        };
        root.Items.Add(new Separator());
        root.Items.Add(inCompact);
        return root;
    }

    /// <summary>A submenu of mutually exclusive options that saves and applies the choice.</summary>
    private MenuItem CreateRadioMenu<T>(string header, (T Value, string Name)[] options, Func<T> get, Action<T> set,
        Action? apply = null)
        where T : struct, Enum
    {
        apply ??= () => _window?.ApplyGradientSettings();
        var root = new MenuItem { Header = header };
        var items = new List<MenuItem>();
        foreach (var (value, name) in options)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = get().Equals(value) };
            item.Click += (_, _) =>
            {
                set(value);
                foreach (var other in items)
                    other.IsChecked = ReferenceEquals(other, item);
                _settings.Save();
                apply();
            };
            items.Add(item);
            root.Items.Add(item);
        }
        return root;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _fullscreen?.Dispose();
        _viewModel?.Dispose();
        _notifications?.Dispose();
        _media?.Dispose();
        _volume?.Dispose();
        _brightness?.Dispose();
        _battery?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
