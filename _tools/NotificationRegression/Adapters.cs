// Only the OS delivery boundary is replaced. Tests execute the shipping gateway.
namespace Microsoft.Windows.AppNotifications
{
    public sealed class AppNotification
    {
        public string Tag { get; set; } = "";
        public string Group { get; set; } = "";
    }
    public sealed class AppNotificationManager
    {
        public static AppNotificationManager Default { get; } = new();
        public List<AppNotification> Sent { get; } = new();
        public bool Fail { get; set; }
        public Action? BeforeShow { get; set; }
        public void Show(AppNotification notification)
        {
            BeforeShow?.Invoke();
            if (Fail) throw new InvalidOperationException("OS notification delivery unavailable");
            Sent.Add(notification);
        }
    }
}
namespace Microsoft.Windows.AppNotifications.Builder
{
    public sealed class AppNotificationBuilder
    {
        public AppNotificationBuilder AddText(string text) => this;
        public AppNotification BuildNotification() => new();
    }
}
