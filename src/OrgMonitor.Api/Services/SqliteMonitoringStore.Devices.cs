using System.Globalization;
using Microsoft.Data.Sqlite;
using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services;

public sealed partial class SqliteMonitoringStore
{
    private const string DeviceColumns = """
        id, name, hostname, address, kind, operating_system, agent_version,
        last_seen, status, cpu_percent, memory_percent, disk_percent, tags
        """;

    public IReadOnlyList<ManagedDevice> GetDevices()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {DeviceColumns} FROM devices ORDER BY name COLLATE NOCASE;";

        using var reader = command.ExecuteReader();
        var devices = new List<ManagedDevice>();
        while (reader.Read())
        {
            devices.Add(ReadDevice(reader));
        }

        return devices;
    }

    public ManagedDevice? GetDevice(Guid id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {DeviceColumns} FROM devices WHERE id = $id;";
        AddGuid(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDevice(reader) : null;
    }

    /// <summary>Records a heartbeat, upserting the device row and appending a metric sample.</summary>
    public ManagedDevice UpsertDevice(AgentHeartbeat heartbeat)
    {
        lock (_writeGate)
        {
            var id = heartbeat.DeviceId == Guid.Empty ? Guid.NewGuid() : heartbeat.DeviceId;
            var existing = GetDevice(id);
            var now = DateTimeOffset.UtcNow;
            var device = new ManagedDevice
            {
                Id = id,
                Name = FirstNonEmpty(heartbeat.Name, existing?.Name, "Unnamed device"),
                Hostname = FirstNonEmpty(heartbeat.Hostname, existing?.Hostname, "unknown"),
                Address = FirstNonEmpty(heartbeat.Address, existing?.Address, "unknown"),
                Kind = heartbeat.Kind == DeviceKind.Unknown ? existing?.Kind ?? DeviceKind.Workstation : heartbeat.Kind,
                OperatingSystem = FirstNonEmpty(heartbeat.OperatingSystem, existing?.OperatingSystem, "Unknown"),
                AgentVersion = FirstNonEmpty(heartbeat.AgentVersion, existing?.AgentVersion, "unknown"),
                LastSeen = now,
                Status = HealthStatus.Online,
                CpuPercent = heartbeat.CpuPercent ?? existing?.CpuPercent,
                MemoryPercent = heartbeat.MemoryPercent ?? existing?.MemoryPercent,
                DiskPercent = heartbeat.DiskPercent ?? existing?.DiskPercent,
                Tags = FirstNonEmpty(heartbeat.Tags, existing?.Tags, string.Empty)
            };

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO devices
                        (id, name, hostname, address, kind, operating_system, agent_version,
                         last_seen, status, cpu_percent, memory_percent, disk_percent, tags)
                    VALUES
                        ($id, $name, $hostname, $address, $kind, $operating_system, $agent_version,
                         $last_seen, $status, $cpu_percent, $memory_percent, $disk_percent, $tags)
                    ON CONFLICT(id) DO UPDATE SET
                        name = excluded.name,
                        hostname = excluded.hostname,
                        address = excluded.address,
                        kind = excluded.kind,
                        operating_system = excluded.operating_system,
                        agent_version = excluded.agent_version,
                        last_seen = excluded.last_seen,
                        status = excluded.status,
                        cpu_percent = excluded.cpu_percent,
                        memory_percent = excluded.memory_percent,
                        disk_percent = excluded.disk_percent,
                        tags = excluded.tags;
                    """;
                AddText(command, "$id", device.Id.ToString());
                AddText(command, "$name", device.Name);
                AddText(command, "$hostname", device.Hostname);
                AddText(command, "$address", device.Address);
                command.Parameters.AddWithValue("$kind", (int)device.Kind);
                AddText(command, "$operating_system", device.OperatingSystem);
                AddText(command, "$agent_version", device.AgentVersion);
                AddText(command, "$last_seen", FormatDate(device.LastSeen!.Value));
                command.Parameters.AddWithValue("$status", (int)device.Status);
                AddNullableDouble(command, "$cpu_percent", device.CpuPercent);
                AddNullableDouble(command, "$memory_percent", device.MemoryPercent);
                AddNullableDouble(command, "$disk_percent", device.DiskPercent);
                AddText(command, "$tags", device.Tags);
                command.ExecuteNonQuery();
            }

            using (var metric = connection.CreateCommand())
            {
                metric.Transaction = transaction;
                metric.CommandText = """
                    INSERT INTO device_metrics (device_id, captured_at, cpu_percent, memory_percent, disk_percent)
                    VALUES ($device_id, $captured_at, $cpu, $memory, $disk);
                    """;
                AddText(metric, "$device_id", device.Id.ToString());
                AddText(metric, "$captured_at", FormatDate(now));
                AddNullableDouble(metric, "$cpu", device.CpuPercent);
                AddNullableDouble(metric, "$memory", device.MemoryPercent);
                AddNullableDouble(metric, "$disk", device.DiskPercent);
                metric.ExecuteNonQuery();
            }

            transaction.Commit();
            return device;
        }
    }

    /// <summary>
    /// Adds a manually tracked device. It has no agent, so <c>last_seen</c> stays null
    /// and its status is <see cref="HealthStatus.Unknown"/> until something reports for it.
    /// </summary>
    public ManagedDevice CreateDevice(CreateDeviceRequest request)
    {
        lock (_writeGate)
        {
            var device = new ManagedDevice
            {
                Id = Guid.NewGuid(),
                Name = request.Name!.Trim(),
                Hostname = Pick(request.Hostname, "unknown"),
                Address = request.Address!.Trim(),
                Kind = request.Kind,
                OperatingSystem = Pick(request.OperatingSystem, "Unknown"),
                AgentVersion = string.Empty,
                LastSeen = null,
                Status = HealthStatus.Unknown,
                Tags = request.Tags?.Trim() ?? string.Empty
            };

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO devices
                    (id, name, hostname, address, kind, operating_system, agent_version,
                     last_seen, status, cpu_percent, memory_percent, disk_percent, tags)
                VALUES
                    ($id, $name, $hostname, $address, $kind, $operating_system, $agent_version,
                     NULL, $status, NULL, NULL, NULL, $tags);
                """;
            AddText(command, "$id", device.Id.ToString());
            AddText(command, "$name", device.Name);
            AddText(command, "$hostname", device.Hostname);
            AddText(command, "$address", device.Address);
            command.Parameters.AddWithValue("$kind", (int)device.Kind);
            AddText(command, "$operating_system", device.OperatingSystem);
            AddText(command, "$agent_version", device.AgentVersion);
            command.Parameters.AddWithValue("$status", (int)device.Status);
            AddText(command, "$tags", device.Tags);
            command.ExecuteNonQuery();

            return device;
        }
    }

    public ManagedDevice? UpdateDevice(Guid id, UpdateDeviceRequest request)
    {
        lock (_writeGate)
        {
            var existing = GetDevice(id);
            if (existing is null)
            {
                return null;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE devices
                SET name = $name,
                    hostname = $hostname,
                    address = $address,
                    kind = $kind,
                    operating_system = $operating_system,
                    tags = $tags
                WHERE id = $id;
                """;
            AddGuid(command, "$id", id);
            AddText(command, "$name", Pick(request.Name, existing.Name));
            AddText(command, "$hostname", Pick(request.Hostname, existing.Hostname));
            AddText(command, "$address", Pick(request.Address, existing.Address));
            command.Parameters.AddWithValue("$kind", (int)(request.Kind ?? existing.Kind));
            AddText(command, "$operating_system", Pick(request.OperatingSystem, existing.OperatingSystem));
            AddText(command, "$tags", request.Tags?.Trim() ?? existing.Tags);
            command.ExecuteNonQuery();
            return GetDevice(id);
        }
    }

    public bool DeleteDevice(Guid id)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM devices WHERE id = $id;";
            AddGuid(command, "$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<DeviceMetricPoint> GetDeviceMetrics(Guid deviceId, DateTimeOffset since)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT captured_at, cpu_percent, memory_percent, disk_percent
            FROM device_metrics
            WHERE device_id = $device_id AND captured_at >= $since
            ORDER BY captured_at ASC;
            """;
        AddText(command, "$device_id", deviceId.ToString());
        AddText(command, "$since", FormatDate(since));

        using var reader = command.ExecuteReader();
        var points = new List<DeviceMetricPoint>();
        while (reader.Read())
        {
            points.Add(new DeviceMetricPoint
            {
                CapturedAt = ReadDate(reader, 0) ?? DateTimeOffset.MinValue,
                CpuPercent = ReadDouble(reader, 1),
                MemoryPercent = ReadDouble(reader, 2),
                DiskPercent = ReadDouble(reader, 3)
            });
        }

        return points;
    }

    /// <summary>Marks devices that missed the heartbeat threshold as offline; returns the ones newly marked.</summary>
    public IReadOnlyList<(Guid Id, string Name)> MarkStaleDevicesOffline(TimeSpan staleAfter)
    {
        lock (_writeGate)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoffText = FormatDate(now.Subtract(staleAfter));
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            // Only devices that reported and then went quiet are stale. A manually added
            // device has never reported (last_seen IS NULL), so Unknown stays the honest
            // state for it rather than flipping to Offline.
            lookup.CommandText = """
                SELECT id, name
                FROM devices
                WHERE status <> $offline
                  AND last_seen IS NOT NULL
                  AND last_seen < $cutoff;
                """;
            lookup.Parameters.AddWithValue("$offline", (int)HealthStatus.Offline);
            AddText(lookup, "$cutoff", cutoffText);

            var stale = new List<(Guid Id, string Name)>();
            using (var reader = lookup.ExecuteReader())
            {
                while (reader.Read())
                {
                    stale.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
                }
            }

            foreach (var device in stale)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE devices SET status = $offline WHERE id = $id;";
                update.Parameters.AddWithValue("$offline", (int)HealthStatus.Offline);
                AddGuid(update, "$id", device.Id);
                update.ExecuteNonQuery();
            }

            transaction.Commit();
            return stale;
        }
    }

    /// <summary>Blank or whitespace input leaves the stored value untouched.</summary>
    private static string Pick(string? requested, string current) =>
        string.IsNullOrWhiteSpace(requested) ? current : requested.Trim();
}
