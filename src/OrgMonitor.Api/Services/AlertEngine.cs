using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OrgMonitor.Api.Models;
using OrgMonitor.Api.Services.Events;

namespace OrgMonitor.Api.Services;

/// <summary>
/// Turns monitoring state changes into alerts, driven entirely by the configurable
/// alert rules. Suppressed during maintenance windows; duplicates are collapsed via
/// dedupe keys; recovery resolves the corresponding open alert.
/// </summary>
public sealed class AlertEngine
{
    private readonly SqliteMonitoringStore _store;
    private readonly MonitoringEventBus _events;
    private readonly NotificationDispatcher _notifications;
    private readonly ILogger<AlertEngine> _logger;

    // (entity, rule) -> consecutive breaching samples
    private readonly ConcurrentDictionary<(Guid Entity, Guid Rule), int> _streaks = new();

    public AlertEngine(
        SqliteMonitoringStore store,
        MonitoringEventBus events,
        NotificationDispatcher notifications,
        ILogger<AlertEngine> logger)
    {
        _store = store;
        _events = events;
        _notifications = notifications;
        _logger = logger;
    }

    public void OnDeviceOffline(Guid deviceId, string deviceName)
    {
        var rule = EnabledRule(AlertMetric.offline);
        if (rule is null)
        {
            return;
        }

        Raise(new MonitorAlert
        {
            Id = Guid.NewGuid(),
            Severity = rule.Severity,
            Title = $"{deviceName} stopped reporting",
            Message = string.IsNullOrWhiteSpace(rule.Description)
                ? "The managed device missed the heartbeat offline threshold."
                : rule.Description,
            DeviceId = deviceId,
            RuleId = rule.Id,
            EntityName = deviceName,
            CreatedAt = DateTimeOffset.UtcNow
        }, $"device:{deviceId}:offline", deviceId, null);
    }

    public void OnDeviceRecovered(Guid deviceId, string deviceName)
    {
        if (Resolve($"device:{deviceId}:offline", deviceId, null))
        {
            _logger.LogInformation("Resolved offline alert for {Device}", deviceName);
        }
    }

    /// <summary>Evaluates cpu/memory/disk rules against the latest heartbeat sample.</summary>
    public void OnDeviceMetrics(ManagedDevice device)
    {
        if (device.Status == HealthStatus.Offline)
        {
            return;
        }

        var rules = _store.GetAlertRules().Where(rule =>
            rule.Enabled && rule.Metric is AlertMetric.cpu or AlertMetric.memory or AlertMetric.disk);

        foreach (var rule in rules)
        {
            if (rule.ScopeKind is { } scope && scope != device.Kind)
            {
                continue;
            }

            var value = rule.Metric switch
            {
                AlertMetric.cpu => device.CpuPercent,
                AlertMetric.memory => device.MemoryPercent,
                _ => device.DiskPercent
            };

            if (value is null)
            {
                continue;
            }

            var key = (device.Id, rule.Id);
            if (!Breaches(value.Value, rule.Comparator, rule.Threshold))
            {
                _streaks[key] = 0;
                Resolve($"device:{device.Id}:rule:{rule.Id}", device.Id, null);
                continue;
            }

            var streak = _streaks.AddOrUpdate(key, 1, (_, current) => current + 1);
            if (streak < rule.ConsecutiveBreaches)
            {
                continue;
            }

            var label = rule.Metric switch
            {
                AlertMetric.cpu => "CPU usage",
                AlertMetric.memory => "Memory usage",
                _ => "Disk usage"
            };

            Raise(new MonitorAlert
            {
                Id = Guid.NewGuid(),
                Severity = rule.Severity,
                Title = $"{device.Name}: {rule.Name}",
                Message = $"{label} is {value.Value:F1}% (rule: {DescribeComparator(rule.Comparator)} {rule.Threshold}).",
                DeviceId = device.Id,
                RuleId = rule.Id,
                EntityName = device.Name,
                CreatedAt = DateTimeOffset.UtcNow
            }, $"device:{device.Id}:rule:{rule.Id}", device.Id, null);
        }
    }

    public void OnProbeResult(NetworkTarget target, ProbePersistence persistence, HealthStatus status, double? latencyMs, string? error)
    {
        if (!persistence.Found)
        {
            return;
        }

        var transitioned = persistence.PreviousStatus != status;

        if (status == HealthStatus.Offline)
        {
            var rule = EnabledRule(AlertMetric.probe_failure);
            if (rule is null || !transitioned)
            {
                return;
            }

            Raise(new MonitorAlert
            {
                Id = Guid.NewGuid(),
                Severity = rule.Severity,
                Title = $"{target.Name} is unreachable",
                Message = string.IsNullOrWhiteSpace(error)
                    ? (string.IsNullOrWhiteSpace(rule.Description)
                        ? "The configured network probe could not reach the target."
                        : rule.Description)
                    : $"The configured network probe could not reach the target: {error}",
                TargetId = target.Id,
                RuleId = rule.Id,
                EntityName = target.Name,
                CreatedAt = DateTimeOffset.UtcNow
            }, $"target:{target.Id}:offline", null, target.Id);
            return;
        }

        if (status == HealthStatus.Online)
        {
            Resolve($"target:{target.Id}:offline", null, target.Id);
        }

        if (latencyMs is null)
        {
            return;
        }

        foreach (var rule in _store.GetAlertRules()
                     .Where(rule => rule.Enabled && rule.Metric == AlertMetric.probe_latency_ms))
        {
            var key = (target.Id, rule.Id);
            if (!Breaches(latencyMs.Value, rule.Comparator, rule.Threshold))
            {
                _streaks[key] = 0;
                Resolve($"target:{target.Id}:rule:{rule.Id}", null, target.Id);
                continue;
            }

            var streak = _streaks.AddOrUpdate(key, 1, (_, current) => current + 1);
            if (streak < rule.ConsecutiveBreaches)
            {
                continue;
            }

            Raise(new MonitorAlert
            {
                Id = Guid.NewGuid(),
                Severity = rule.Severity,
                Title = $"{target.Name}: {rule.Name}",
                Message = $"Probe latency is {latencyMs.Value:F0}ms (rule: {DescribeComparator(rule.Comparator)} {rule.Threshold:F0}ms).",
                TargetId = target.Id,
                RuleId = rule.Id,
                EntityName = target.Name,
                CreatedAt = DateTimeOffset.UtcNow
            }, $"target:{target.Id}:rule:{rule.Id}", null, target.Id);
        }
    }

    public void OnAccessPointOffline(Guid accessPointId, string accessPointName)
    {
        var rule = EnabledRule(AlertMetric.offline);
        if (rule is null)
        {
            return;
        }

        Raise(new MonitorAlert
        {
            Id = Guid.NewGuid(),
            Severity = rule.Severity,
            Title = $"{accessPointName} is offline",
            Message = "The wireless controller has not reported this access point recently.",
            RuleId = rule.Id,
            EntityName = accessPointName,
            CreatedAt = DateTimeOffset.UtcNow
        }, $"ap:{accessPointId}:offline", null, null);
    }

    public void OnAccessPointRecovered(Guid accessPointId) =>
        Resolve($"ap:{accessPointId}:offline", null, null);

    // ---- helpers -------------------------------------------------------------

    private AlertRule? EnabledRule(AlertMetric metric) =>
        _store.GetAlertRules().FirstOrDefault(rule => rule.Enabled && rule.Metric == metric);

    private void Raise(MonitorAlert alert, string dedupeKey, Guid? deviceId, Guid? targetId)
    {
        if (Suppressed(deviceId, targetId))
        {
            return;
        }

        try
        {
            var created = _store.CreateAlert(alert, dedupeKey);
            if (created.Id != alert.Id)
            {
                return; // an equivalent alert is already open
            }

            _events.Publish("alert.created", alert.Id);
            _notifications.Enqueue(alert);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not create alert {Title}", alert.Title);
        }
    }

    private bool Resolve(string dedupeKey, Guid? deviceId, Guid? targetId)
    {
        if (Suppressed(deviceId, targetId))
        {
            return false;
        }

        try
        {
            var resolved = _store.ResolveAlertsByDedupeKey(dedupeKey, DateTimeOffset.UtcNow);
            if (resolved > 0)
            {
                _events.Publish("alert.resolved");
                return true;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not resolve alerts for {Key}", dedupeKey);
        }

        return false;
    }

    private bool Suppressed(Guid? deviceId, Guid? targetId)
    {
        if (_store.IsMaintenanceActive(MaintenanceScope.all))
        {
            return true;
        }

        if (deviceId is { } device && _store.IsMaintenanceActive(MaintenanceScope.device, device))
        {
            return true;
        }

        if (targetId is { } target && _store.IsMaintenanceActive(MaintenanceScope.target, target))
        {
            return true;
        }

        return false;
    }

    private static bool Breaches(double value, AlertComparator comparator, double threshold) => comparator switch
    {
        AlertComparator.gt => value > threshold,
        AlertComparator.gte => value >= threshold,
        AlertComparator.lt => value < threshold,
        _ => value <= threshold
    };

    private static string DescribeComparator(AlertComparator comparator) => comparator switch
    {
        AlertComparator.gt => ">",
        AlertComparator.gte => "≥",
        AlertComparator.lt => "<",
        _ => "≤"
    };
}
