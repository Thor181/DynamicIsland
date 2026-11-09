using DynamicIsland.Settings;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Services;

public sealed record NotificationInfo(string AppName, string Title, string Body);

/// <summary>
/// Mirrors Windows toast notifications. Requires the app to run with package identity
/// (MSIX or sparse package) and the user to grant notification access; otherwise it
/// stays disabled and <see cref="StartAsync"/> returns false.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    private readonly CancellationTokenSource _cts = new();
    private UserNotificationListener? _listener;
    private HashSet<uint> _seen = [];

    public event Action<NotificationInfo>? NotificationReceived;

    public bool IsActive { get; private set; }

    public async Task<bool> StartAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var status = await _listener.RequestAccessAsync();
            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                Log.Write($"Notification access: {status}");
                return false;
            }

            var initial = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            _seen = initial.Select(n => n.Id).ToHashSet();
            IsActive = true;
            Log.Write("Notification listener active");
            _ = PollLoopAsync(_cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Notifications unavailable (app needs package identity): {ex.Message}");
            return false;
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    var current = await _listener!.GetNotificationsAsync(NotificationKinds.Toast);
                    var ids = new HashSet<uint>();
                    foreach (var n in current)
                    {
                        ids.Add(n.Id);
                        if (!_seen.Contains(n.Id) && TryRead(n) is { } info)
                            NotificationReceived?.Invoke(info);
                    }
                    _seen = ids;
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static NotificationInfo? TryRead(UserNotification n)
    {
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (texts is null || texts.Count == 0) return null;

            string app;
            try { app = n.AppInfo.DisplayInfo.DisplayName; }
            catch { app = "Уведомление"; }

            return new NotificationInfo(app, texts[0], string.Join(" ", texts.Skip(1)));
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
