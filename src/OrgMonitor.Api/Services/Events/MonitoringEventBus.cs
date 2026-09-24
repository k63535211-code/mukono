using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services.Events;

public sealed record MonitoringEvent(string Type, Guid? EntityId, DateTimeOffset OccurredAt);

/// <summary>
/// In-process fan-out for server-sent events. Each subscriber gets its own bounded
/// queue so slow browsers never block producers or other subscribers.
/// </summary>
public sealed class MonitoringEventBus
{
    private readonly object _gate = new();
    private readonly List<Channel<MonitoringEvent>> _subscribers = new();

    public void Publish(string type, Guid? entityId = null)
    {
        var monitoringEvent = new MonitoringEvent(type, entityId, DateTimeOffset.UtcNow);
        Channel<MonitoringEvent>[] subscribers;
        lock (_gate)
        {
            subscribers = _subscribers.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryWrite(monitoringEvent);
        }
    }

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<MonitoringEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        return new Subscription(this, channel);
    }

    /// <summary>Next event for one subscriber, or null when the subscription/request ends.</summary>
    public async ValueTask<MonitoringEvent?> ReadNextAsync(ChannelReader<MonitoringEvent> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                if (reader.TryRead(out var monitoringEvent))
                {
                    return monitoringEvent;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // request aborted
        }

        return null;
    }

    private void Unsubscribe(Channel<MonitoringEvent> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    public sealed class Subscription : System.IDisposable
    {
        private readonly MonitoringEventBus _bus;
        private readonly Channel<MonitoringEvent> _channel;

        internal Subscription(MonitoringEventBus bus, Channel<MonitoringEvent> channel)
        {
            _bus = bus;
            _channel = channel;
        }

        public ChannelReader<MonitoringEvent> Reader => _channel.Reader;

        public void Dispose() => _bus.Unsubscribe(_channel);
    }
}
