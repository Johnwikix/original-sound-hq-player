using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.AppNotifications;
using System.Reflection;
using WinUIMusicPlayer.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

using var gateway = new NotificationService(NullLogger<NotificationService>.Instance);
var os = AppNotificationManager.Default;
Parallel.For(0, 100, _ => gateway.SendNotification("Atmos", "Busy", "atmos-busy"));
Check(os.Sent.Count == 1, "concurrent identical failures produce one system notification");
gateway.SendNotification("Atmos", "Disconnected", "atmos-disconnected");
Check(os.Sent.Count == 2, "a different failure is not suppressed");
os.Fail = true;
gateway.SendNotification("Metadata", "Could not write", "metadata");
os.Fail = false;
gateway.SendNotification("Metadata", "Could not write", "metadata");
Check(os.Sent.Count == 3, "OS failure is non-fatal and does not suppress a later successful delivery");
var cache = (Dictionary<string, long>)typeof(NotificationService).GetField("_lastSent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!;
cache[os.Sent[0].Tag] = Environment.TickCount64 - 31_000;
gateway.SendNotification("Atmos", "Busy again", "atmos-busy");
Check(os.Sent.Count == 4 && os.Sent[0].Tag == os.Sent[3].Tag, "cooldown expires and stable tag replaces the prior notification");
for (int i = 0; i < 300; i++) gateway.SendNotification("Failure", "Distinct", "key-" + i);
Check(cache.Count == 128, "deduplication storage is bounded");
using var entered = new ManualResetEventSlim();
using var release = new ManualResetEventSlim();
os.BeforeShow = () => { entered.Set(); release.Wait(); };
var send = Task.Run(() => gateway.SendNotification("Last", "In flight", "last"));
Check(entered.Wait(2000), "delivery entered the OS boundary");
var dispose = Task.Run(gateway.Dispose);
release.Set();
await Task.WhenAll(send, dispose);
int finalCount = os.Sent.Count;
gateway.Dispose();
gateway.SendNotification("Late", "Must not send", "late");
Check(os.Sent.Count == finalCount && cache.Count == 0, "shutdown finishes in-flight delivery and rejects late sends idempotently");
Console.WriteLine("Notification regression: 7/7 passed (OS boundary simulated).");
