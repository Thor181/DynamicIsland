using System.Management;
using DynamicIsland.Settings;

namespace DynamicIsland.Services;

/// <summary>
/// Listens for built-in display brightness changes via WMI (laptops/tablets only;
/// silently does nothing on desktops with external monitors).
/// <see cref="BrightnessChanged"/> is raised on a background thread.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private ManagementEventWatcher? _watcher;

    public event Action<int>? BrightnessChanged;

    public void Start()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            var query = new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
            _watcher = new ManagementEventWatcher(scope, query);
            _watcher.EventArrived += OnEventArrived;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"Brightness monitoring unavailable: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            int value = Convert.ToInt32(e.NewEvent.Properties["Brightness"].Value);
            BrightnessChanged?.Invoke(value);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    public void Dispose()
    {
        if (_watcher is null) return;
        try
        {
            _watcher.EventArrived -= OnEventArrived;
            _watcher.Stop();
        }
        catch { }
        _watcher.Dispose();
        _watcher = null;
    }
}
