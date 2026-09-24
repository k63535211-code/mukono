using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services;

public sealed partial class SqliteMonitoringStore
{
    /// <summary>Aggregate counters for the dashboard overview tile.</summary>
    public Overview GetOverview()
    {
        var devices = GetDevices();
        var targets = GetNetworkTargets();
        var openAlerts = GetAlerts(includeAcknowledged: true);
        var wireless = GetAccessPoints();
        var now = DateTimeOffset.UtcNow;

        var activeMaintenance = GetMaintenanceWindows()
            .Count(window => window.StartsAt <= now && window.EndsAt >= now);

        return new Overview(
            devices.Count,
            devices.Count(device => device.Status == HealthStatus.Online),
            devices.Count(device => device.Status == HealthStatus.Warning),
            devices.Count(device => device.Status == HealthStatus.Offline),
            targets.Count,
            targets.Count(target => target.Status == HealthStatus.Online),
            openAlerts.Count(alert => !alert.Acknowledged),
            openAlerts.Count(alert => !alert.Acknowledged && alert.Severity == AlertSeverity.critical),
            wireless.Count,
            wireless.Sum(accessPoint => accessPoint.Clients ?? 0),
            activeMaintenance,
            GetAvailability(TimeSpan.FromHours(24)),
            now);
    }
}
