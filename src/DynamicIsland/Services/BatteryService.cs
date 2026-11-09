using Windows.System.Power;

namespace DynamicIsland.Services;

public readonly record struct BatteryState(bool HasBattery, int Percent, bool IsPluggedIn);

/// <summary>Battery level and power source. <see cref="Changed"/> is raised on a background thread.</summary>
public sealed class BatteryService : IDisposable
{
    public event Action<BatteryState>? Changed;

    public BatteryState Current => new(
        PowerManager.BatteryStatus != BatteryStatus.NotPresent,
        PowerManager.RemainingChargePercent,
        PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent);

    public void Start()
    {
        PowerManager.RemainingChargePercentChanged += OnChanged;
        PowerManager.PowerSupplyStatusChanged += OnChanged;
        PowerManager.BatteryStatusChanged += OnChanged;
    }

    private void OnChanged(object? sender, object e) => Changed?.Invoke(Current);

    public void Dispose()
    {
        PowerManager.RemainingChargePercentChanged -= OnChanged;
        PowerManager.PowerSupplyStatusChanged -= OnChanged;
        PowerManager.BatteryStatusChanged -= OnChanged;
    }
}
