namespace OrgMonitor.Api.Models;

/// <summary>
/// An access point reported by an authorized wireless controller or collector.
/// OrgMonitor never sniffs traffic; this inventory arrives via authenticated push.
/// </summary>
public sealed class WirelessAccessPoint
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Site { get; init; } = string.Empty;
    public string Band { get; init; } = string.Empty;
    public int Channel { get; init; }
    public int? Clients { get; init; }
    public double? UtilizationPercent { get; init; }
    public double? NoiseFloorDbm { get; init; }
    public double? SnrDb { get; init; }
    public string Firmware { get; init; } = string.Empty;
    public string Controller { get; init; } = string.Empty;
    public HealthStatus Status { get; init; } = HealthStatus.Unknown;
    public DateTimeOffset LastSeen { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed class WirelessApSnapshot
{
    /// <summary>Optional stable identifier (for example a BSSID). When omitted one is derived from site/name.</summary>
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Site { get; init; }
    public string? Band { get; init; }
    public int? Channel { get; init; }
    public int? Clients { get; init; }
    public double? UtilizationPercent { get; init; }
    public double? NoiseFloorDbm { get; init; }
    public double? SnrDb { get; init; }
    public string? Firmware { get; init; }
    public HealthStatus? Status { get; init; }
    public string? Notes { get; init; }
}

public sealed class WirelessIngestRequest
{
    public string? Controller { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public List<WirelessApSnapshot>? AccessPoints { get; init; }
}

public sealed record WirelessIngestResult(int Received, int Updated, DateTimeOffset CapturedAt);

public sealed class WirelessApHistoryPoint
{
    public DateTimeOffset CapturedAt { get; init; }
    public int? Clients { get; init; }
    public double? UtilizationPercent { get; init; }
    public double? NoiseFloorDbm { get; init; }
    public double? SnrDb { get; init; }
}

public sealed record WirelessSummary(
    int TotalAccessPoints,
    int OnlineAccessPoints,
    int WarningAccessPoints,
    int OfflineAccessPoints,
    int TotalClients,
    double? AverageUtilizationPercent,
    int BusyChannels,
    DateTimeOffset GeneratedAt);
