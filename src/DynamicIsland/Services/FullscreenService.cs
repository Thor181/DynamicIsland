using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>Polls whether a fullscreen app is in the foreground. Raises <see cref="Changed"/> on the UI thread.</summary>
public sealed class FullscreenService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public bool IsFullscreen { get; private set; }

    public event Action<bool>? Changed;

    public FullscreenService() => _timer.Tick += (_, _) => Check();

    public void Start()
    {
        Check();
        _timer.Start();
    }

    private void Check()
    {
        bool fullscreen = NativeMethods.IsFullscreenAppRunning();
        if (fullscreen == IsFullscreen) return;
        IsFullscreen = fullscreen;
        Changed?.Invoke(fullscreen);
    }

    public void Dispose() => _timer.Stop();
}
