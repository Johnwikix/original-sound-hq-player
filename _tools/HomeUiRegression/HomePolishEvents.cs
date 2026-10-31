using System.Diagnostics.Tracing;

[EventSource(Name = "HomePolishProbe")]
internal sealed class HomePolishEvents : EventSource
{
    internal static readonly HomePolishEvents Log = new();
    [Event(1, Level = EventLevel.Informational)]
    public void Phase(string phase) => WriteEvent(1, phase);
}
