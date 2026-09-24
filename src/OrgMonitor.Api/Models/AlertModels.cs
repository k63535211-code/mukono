namespace OrgMonitor.Api.Models;

/// <summary>Serialized as lowercase severity names ("info", "warning", "critical").</summary>
public enum AlertSeverity
{
    info = 0,
    warning = 1,
    critical = 2
}

/// <summary>Serialized as lowercase metric names matching the alerting vocabulary used by the dashboard.</summary>
public enum AlertMetric
{
    cpu = 0,
    memory = 1,
    disk = 2,
    offline = 3,
    probe_failure = 4,
    probe_latency_ms = 5
}

public enum AlertComparator
{
    gt = 0,
    gte = 1,
    lt = 2,
    lte = 3
}

public sealed class AlertRule
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public AlertMetric Metric { get; init; }
    public AlertComparator Comparator { get; init; } = AlertComparator.gte;
    public double Threshold { get; init; }
    public AlertSeverity Severity { get; init; } = AlertSeverity.warning;
    /// <summary>Optional device-kind scope. Null applies the rule to every device.</summary>
    public DeviceKind? ScopeKind { get; init; }
    /// <summary>Number of consecutive breaching samples required before alerting.</summary>
    public int ConsecutiveBreaches { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public string Description { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class CreateAlertRuleRequest
{
    public string? Name { get; init; }
    public AlertMetric Metric { get; init; }
    public AlertComparator Comparator { get; init; } = AlertComparator.gte;
    public double Threshold { get; init; }
    public AlertSeverity Severity { get; init; } = AlertSeverity.warning;
    public DeviceKind? ScopeKind { get; init; }
    public int ConsecutiveBreaches { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public string? Description { get; init; }
}

public sealed class UpdateAlertRuleRequest
{
    public string? Name { get; init; }
    public double? Threshold { get; init; }
    public AlertComparator? Comparator { get; init; }
    public AlertSeverity? Severity { get; init; }
    public DeviceKind? ScopeKind { get; init; }
    public int? ConsecutiveBreaches { get; init; }
    public bool? Enabled { get; init; }
    public string? Description { get; init; }
    /// <summary>When true the scope is explicitly cleared back to "all devices".</summary>
    public bool ClearScope { get; init; }
}

/// <summary>Serialized as lowercase scope names ("all", "device", "target").</summary>
public enum MaintenanceScope
{
    all = 0,
    device = 1,
    target = 2
}

public sealed class MaintenanceWindow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public MaintenanceScope Scope { get; init; } = MaintenanceScope.all;
    public Guid? ScopeId { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }

    public bool IsActive =>
        StartsAt <= DateTimeOffset.UtcNow && EndsAt >= DateTimeOffset.UtcNow;
}

public sealed class CreateMaintenanceWindowRequest
{
    public string? Name { get; init; }
    public MaintenanceScope Scope { get; init; } = MaintenanceScope.all;
    public Guid? ScopeId { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public string? Reason { get; init; }
}

public enum NotificationChannelType
{
    webhook = 0
}

public sealed class NotificationChannel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public NotificationChannelType Type { get; init; } = NotificationChannelType.webhook;
    public string Url { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public AlertSeverity MinSeverity { get; init; } = AlertSeverity.warning;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastDeliveryAt { get; init; }
    public bool? LastDeliverySuccess { get; init; }
}

public sealed class CreateNotificationChannelRequest
{
    public string? Name { get; init; }
    public string? Url { get; init; }
    public bool Enabled { get; init; } = true;
    public AlertSeverity MinSeverity { get; init; } = AlertSeverity.warning;
}

public sealed class UpdateNotificationChannelRequest
{
    public string? Name { get; init; }
    public string? Url { get; init; }
    public bool? Enabled { get; init; }
    public AlertSeverity? MinSeverity { get; init; }
}

public sealed class NotificationDelivery
{
    public long Id { get; init; }
    public Guid ChannelId { get; init; }
    public Guid? AlertId { get; init; }
    public DateTimeOffset AttemptedAt { get; init; }
    public bool Success { get; init; }
    public int? StatusCode { get; init; }
    public string? Error { get; init; }
}
