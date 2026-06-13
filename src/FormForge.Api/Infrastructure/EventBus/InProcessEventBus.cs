using System.Collections.Concurrent;

namespace FormForge.Api.Infrastructure.EventBus;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed partial class InProcessEventBus(ILogger<InProcessEventBus> logger) : IDomainEventBus
{
    private readonly ConcurrentDictionary<Type, List<Action<object>>> _handlers = new();

    public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        var list = _handlers.GetOrAdd(typeof(TEvent), _ => []);
        lock (list)
        {
            list.Add(e => handler((TEvent)e));
        }
    }

    public void Publish<TEvent>(TEvent @event) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (!_handlers.TryGetValue(typeof(TEvent), out var list))
        {
            return;
        }

        // Snapshot under the lock, then execute outside — so a handler that
        // calls Subscribe/Publish re-entrantly cannot deadlock.
        List<Action<object>> snapshot;
        lock (list)
        {
            snapshot = [.. list];
        }

        foreach (var handler in snapshot)
        {
            // Isolate handler failures from the publisher. A subscriber that
            // throws must not roll back the commit (or surface a 500) of the
            // caller that already succeeded. Log and continue so other
            // subscribers still get the event. (Story 2.6 review patch #8.)
            try
            {
                handler(@event);
            }
#pragma warning disable CA1031 // Generic catch is intentional: the bus must isolate any subscriber failure.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogHandlerThrew(logger, typeof(TEvent).Name, ex);
            }
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        EventId = 6001,
        Level = Microsoft.Extensions.Logging.LogLevel.Error,
        Message = "InProcessEventBus subscriber threw while handling {EventType}; other subscribers and the publisher continue.")]
    private static partial void LogHandlerThrew(
        Microsoft.Extensions.Logging.ILogger logger,
        string eventType,
        Exception ex);
}
