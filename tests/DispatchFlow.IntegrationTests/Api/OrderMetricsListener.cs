using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using DispatchFlow.Application.Orders;
using Microsoft.Extensions.DependencyInjection;

namespace DispatchFlow.IntegrationTests.Api;

internal sealed class OrderMetricsListener : IDisposable {
    private readonly ConcurrentDictionary<string, long> _counts = new();
    private readonly MeterListener _listener;
    private int _taggedMeasurements;

    public OrderMetricsListener(IServiceProvider services) {
        var meter = services.GetRequiredService<IMeterFactory>().Create(new MeterOptions(OrderMetrics.METER_NAME));
        _listener = new MeterListener {
            InstrumentPublished = (instrument, observer) => {
                // Each test host has its own Meter, even when other hosts use the same name.
                if (ReferenceEquals(instrument.Meter, meter)) {
                    observer.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
            if (!tags.IsEmpty) {
                Interlocked.Increment(ref _taggedMeasurements);
            }
            _counts.AddOrUpdate(instrument.Name, value, (_, count) => count + value);
        });
        _listener.Start();
    }

    public void AssertCounts(long created = 0, long cancelled = 0, long replayed = 0, long capacityRejected = 0) {
        Assert.Equal(created, _counts.GetValueOrDefault("dispatchflow.orders.created"));
        Assert.Equal(cancelled, _counts.GetValueOrDefault("dispatchflow.orders.cancelled"));
        Assert.Equal(replayed, _counts.GetValueOrDefault("dispatchflow.orders.idempotency_replayed"));
        Assert.Equal(capacityRejected, _counts.GetValueOrDefault("dispatchflow.orders.capacity_rejected"));
        Assert.Equal(0, Volatile.Read(ref _taggedMeasurements));
    }

    public void Dispose() => _listener.Dispose();
}
