using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace WinUIMusicPlayer.Services
{
    public class NotificationService
    {
        public void SendNotification(string title, string content)
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(content)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
    }
}
