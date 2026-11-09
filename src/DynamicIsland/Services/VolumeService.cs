using System.Windows.Threading;
using DynamicIsland.Settings;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicIsland.Services;

/// <summary>
/// Watches the master volume of the default playback device (follows default-device switches).
/// <see cref="VolumeChanged"/> is raised on a background thread.
/// </summary>
public sealed class VolumeService : IMMNotificationClient, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private bool _registered;

    public event Action<float, bool>? VolumeChanged;

    public VolumeService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Start()
    {
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
            _registered = true;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        AttachDefault();
    }

    private void AttachDefault()
    {
        DetachDevice();
        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            _device = null;
        }
    }

    private void DetachDevice()
    {
        if (_device is null) return;
        try
        {
            _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            _device.Dispose();
        }
        catch
        {
            // device may already be gone
        }
        _device = null;
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
        => VolumeChanged?.Invoke(data.MasterVolume, data.Muted);

    /// <summary>Changes the master volume by <paramref name="delta"/> (0..1 scale). Call on the UI thread.</summary>
    public void Adjust(float delta)
    {
        if (_device is null) return;
        try
        {
            var volume = _device.AudioEndpointVolume;
            volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar + delta, 0f, 1f);
            if (delta > 0 && volume.Mute) volume.Mute = false;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    // IMMNotificationClient — don't call Core Audio from inside these callbacks; hop to the UI thread.
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
            _dispatcher.BeginInvoke(AttachDefault);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        DetachDevice();
        if (_registered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        }
        _enumerator.Dispose();
    }
}
