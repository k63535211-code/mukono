namespace OrgMonitor.Api.Models;

public enum DeviceKind
{
    Unknown = 0,
    Workstation = 1,
    Server = 2,
    Router = 3,
    Switch = 4,
    AccessPoint = 5,
    Firewall = 6,
    Printer = 7,
    Other = 8,
    Controller = 9,
    Storage = 10,
    Mobile = 11,
    LoadBalancer = 12
}

public enum HealthStatus
{
    Unknown = 0,
    Online = 1,
    Warning = 2,
    Offline = 3
}

public sealed class AgentHeartbeat
{
    public Guid DeviceId { get; init; }
    public string? Name { get; init; }
    public string? Hostname { get; init; }
    public string? Address { get; init; }
    public DeviceKind Kind { get; init; } = DeviceKind.Workstation;
    public string? OperatingSystem { get; init; }
    public string? AgentVersion { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? DiskPercent { get; init; }
    public string? Tags { get; init; }
}

public sealed class ManagedDevice
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Hostname { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public DeviceKind Kind { get; init; }
    public string OperatingSystem { get; init; } = string.Empty;
    public string AgentVersion { get; init; } = string.Empty;
    public DateTimeOffset? LastSeen { get; init; }
    public HealthStatus Status { get; init; } = HealthStatus.Unknown;
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? DiskPercent { get; init; }
    public string Tags { get; init; } = string.Empty;
}

public sealed class NetworkTarget
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public int Port { get; init; } = 443;
    public string Protocol { get; init; } = "tcp";
    public DeviceKind Kind { get; init; } = DeviceKind.Other;
    public bool Enabled { get; init; } = true;
    public int IntervalSeconds { get; init; } = 60;
    public HealthStatus Status { get; init; } = HealthStatus.Unknown;
    public double? LatencyMs { get; init; }
    public DateTimeOffset? LastChecked { get; init; }
    public string? LastError { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed class MonitorAlert
{
    public Guid Id { get; init; }
    public AlertSeverity Severity { get; init; } = AlertSeverity.info;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public Guid? DeviceId { get; init; }
    public Guid? TargetId { get; init; }
    public Guid? RuleId { get; init; }
    public string EntityName { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public bool Acknowledged { get; init; }
    public string? AcknowledgedBy { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }

    // Kept for dashboard compatibility: resolves to the device or target name.
    public string DeviceName => EntityName;
}

public sealed class CreateNetworkTargetRequest
{
    public string? Name { get; init; }
    public string? Address { get; init; }
    public int Port { get; init; } = 443;
    public string? Protocol { get; init; }
    public DeviceKind Kind { get; init; } = DeviceKind.Other;
    public int? IntervalSeconds { get; init; }
    public string? Notes { get; init; }
}

public sealed class UpdateNetworkTargetRequest
{
    public string? Name { get; init; }
    public string? Address { get; init; }
    public int? Port { get; init; }
    public string? Protocol { get; init; }
    public DeviceKind? Kind { get; init; }
    public bool? Enabled { get; init; }
    public int? IntervalSeconds { get; init; }
    public string? Notes { get; init; }
}

public sealed class UpdateDeviceRequest
{
    public string? Name { get; init; }
    public string? Tags { get; init; }
    public DeviceKind? Kind { get; init; }
}

public sealed class DeviceMetricPoint
{
    public DateTimeOffset CapturedAt { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? DiskPercent { get; init; }
}

public sealed class ProbeResultPoint
{
    public DateTimeOffset CheckedAt { get; init; }
    public HealthStatus Status { get; init; }
    public double? LatencyMs { get; init; }
    public string? Error { get; init; }
}

public sealed record AvailabilitySummary(
    int TotalChecks,
    int SuccessfulChecks,
    double SuccessRatePercent,
    double? AverageLatencyMs,
    double? P95LatencyMs);

/// <summary>Result of a single probe, produced by the worker and persisted by the store.</summary>
public sealed record ProbeOutcome(
    NetworkTarget Target,
    HealthStatus PreviousStatus,
    HealthStatus Status,
    double? LatencyMs,
    string? Error,
    DateTimeOffset CheckedAt);

public sealed record Overview(
    int TotalDevices,
    int OnlineDevices,
    int WarningDevices,
    int OfflineDevices,
    int TotalNetworkTargets,
    int OnlineNetworkTargets,
    int OpenAlerts,
    int CriticalAlerts,
    int WirelessAccessPoints,
    int WirelessClients,
    int ActiveMaintenanceWindows,
    double? Availability24hPercent,
    DateTimeOffset GeneratedAt);

public sealed record SystemInfo(
    string Version,
    string EnvironmentName,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    long DatabaseSizeBytes,
    int SchemaVersion,
    int RetentionDays);
