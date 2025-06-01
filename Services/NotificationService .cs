using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.AppNotifications;

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
