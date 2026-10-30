using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using RateLimiter.Middleware;

namespace RateLimiter.Middleware.Tests;

internal static class TestMetrics
{
    /// <summary>A RateLimiterMetrics with its own meter factory, for tests that don't inspect metrics.</summary>
    public static RateLimiterMetrics Create() => new(NewMeterFactory());

    public static IMeterFactory NewMeterFactory() =>
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
}

/// <summary>
/// Captures measurements from ONE meter factory using the built-in MeterListener.
/// Filtering on Meter.Scope (the factory that created the meter) keeps tests that run
/// in parallel from seeing each other's measurements.
/// </summary>
internal sealed class MetricsRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _measurements = [];
    private readonly Lock _gate = new();

    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    public MetricsRecorder(IMeterFactory scope)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RateLimiterMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, scope))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    public IReadOnlyList<Measurement> For(string instrument)
    {
        lock (_gate)
        {
            return _measurements.Where(m => m.Instrument == instrument).ToList();
        }
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _measurements.Add(new Measurement(instrument.Name, value, copy));
        }
    }

    public void Dispose() => _listener.Dispose();
}
