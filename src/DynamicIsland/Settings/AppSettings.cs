using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DynamicIsland.Settings;

public enum GradientStyle { Off, Aurora, Sunset, Ocean, Forest, Neon, Mono, Cover }

public enum GradientEffect { Drift, Pulse, Flow, Spin, Ring, Shimmer }

/// <summary>Equalizer colours: the accent, the gradient's palette, or a palette of its own.</summary>
public enum EqualizerStyle { Accent, Gradient, Cover, Aurora, Sunset, Ocean, Forest, Neon, Mono }

public sealed class AppSettings
{
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    /// <summary>Distance from the top of the work area, in DIPs.</summary>
    public double TopOffset { get; set; } = 8;

    /// <summary>
    /// Centre of the collapsed island as a fraction (0..1) of the primary screen's width/height.
    /// Null means the default spot: centred at the top, <see cref="TopOffset"/> below the edge.
    /// </summary>
    public double? PositionX { get; set; }
    public double? PositionY { get; set; }

    public bool EnableMedia { get; set; } = true;
    public bool EnableVolume { get; set; } = true;
    public bool EnableBrightness { get; set; } = true;
    public bool EnableBattery { get; set; } = true;
    public bool EnableNotifications { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;

    /// <summary>Palette of the animated background gradient (Off disables it).</summary>
    public GradientStyle Gradient { get; set; } = GradientStyle.Aurora;

    /// <summary>How the gradient moves.</summary>
    public GradientEffect GradientEffect { get; set; } = GradientEffect.Drift;

    /// <summary>Show the gradient on the collapsed pill too, not only when expanded.</summary>
    public bool GradientInCompact { get; set; } = true;

    /// <summary>Colours of the equalizer bars on the collapsed media pill.</summary>
    public EqualizerStyle EqualizerColors { get; set; } = EqualizerStyle.Accent;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }
}

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DynamicIsland";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(object message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(AppSettings.Directory);
                File.AppendAllText(Path.Combine(AppSettings.Directory, "log.txt"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
