using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace WinUIMusicPlayer.Services;

/// <summary>Global notification gateway: bounded deduplication and non-fatal delivery.</summary>
public sealed class NotificationService(ILogger<NotificationService> logger) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastSent = new(StringComparer.Ordinal);
    private bool _disposed;

    public void SendNotification(string title, string content, string? key = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            string tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key ?? title + "\n" + content)))[..16];
            long now = Environment.TickCount64;
            if (_lastSent.TryGetValue(tag, out long previous) && now - previous < 30_000) return;
            try
            {
                var notification = new AppNotificationBuilder().AddText(title).AddText(content).BuildNotification();
                notification.Tag = tag;
                notification.Group = "Player";
                AppNotificationManager.Default.Show(notification);
                if (_lastSent.Count >= 128 && !_lastSent.ContainsKey(tag))
                {
                    string? oldest = null;
                    long oldestTime = long.MaxValue;
                    foreach (var entry in _lastSent)
                    {
                        if (entry.Value >= oldestTime) continue;
                        oldest = entry.Key;
                        oldestTime = entry.Value;
                    }
                    if (oldest != null) _lastSent.Remove(oldest);
                }
                _lastSent[tag] = now;
            }
            catch (Exception ex) { logger.LogWarning(ex, "System notification could not be delivered"); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lastSent.Clear();
        }
    }
}
