using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrgMonitor.Api.Models;
using OrgMonitor.Api.Services.Database;

namespace OrgMonitor.Api.Services;

/// <summary>
/// SQLite-backed monitoring store. Schema changes go through <see cref="Migrations"/>,
/// never through ad-hoc DDL here.
/// </summary>
public sealed partial class SqliteMonitoringStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteMonitoringStore> _logger;
    private readonly object _writeGate = new();

    public string FilePath { get; }
    public int RetentionDays { get; }
    public int AuditRetentionDays { get; }
    public int SchemaVersion { get; private set; }

    public SqliteMonitoringStore(IConfiguration configuration, ILogger<SqliteMonitoringStore> logger)
    {
        _logger = logger;
        RetentionDays = Math.Clamp(configuration.GetValue("Database:RetentionDays", 30), 1, 3650);
        AuditRetentionDays = Math.Clamp(configuration.GetValue("Database:AuditRetentionDays", 90), 1, 3650);

        var configuredPath = configuration["Database:Path"];
        var path = string.IsNullOrWhiteSpace(configuredPath) ? "orgmonitor.db" : configuredPath;

        if (!string.Equals(path, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        FilePath = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
            ForeignKeys = true
        }.ToString();

        Initialize();
        if (configuration.GetValue("Database:SeedDemoData", true))
        {
            SeedIfEmpty();
        }
    }

    public bool IsHealthy()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name IN (
                    'devices', 'network_targets', 'alerts', 'users', 'alert_rules',
                    'device_metrics', 'probe_results', 'wireless_aps', 'sessions');
                """;
            var tables = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return tables == 9 && SchemaVersion >= Migrations.CurrentVersion;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "SQLite health check failed");
            return false;
        }
    }

    public long GetDatabaseSizeBytes()
    {
        if (string.Equals(FilePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        try
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Creates a consistent on-disk copy via SQLite VACUUM INTO and returns its path.</summary>
    public string CreateBackup(string? targetDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(targetDirectory)
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(FilePath)) ?? ".", "backups")
            : Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(directory);

        var fileName = $"orgmonitor-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db";
        var target = Path.Combine(directory, fileName);

        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $target;";
            command.Parameters.AddWithValue("$target", target);
            command.ExecuteNonQuery();
        }

        return target;
    }

    /// <summary>Drops history rows past their retention window.</summary>
    public (int Metrics, int Probes, int WirelessHistory, int Audit) PruneRetention()
    {
        var now = DateTimeOffset.UtcNow;
        var historyCutoff = now.AddDays(-RetentionDays).ToString("O", CultureInfo.InvariantCulture);
        var auditCutoff = now.AddDays(-AuditRetentionDays).ToString("O", CultureInfo.InvariantCulture);

        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            static int Delete(SqliteConnection connection, SqliteTransaction transaction, string sql, string cutoff)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$cutoff", cutoff);
                return command.ExecuteNonQuery();
            }

            var metrics = Delete(connection, transaction,
                "DELETE FROM device_metrics WHERE captured_at < $cutoff;", historyCutoff);
            var probes = Delete(connection, transaction,
                "DELETE FROM probe_results WHERE checked_at < $cutoff;", historyCutoff);
            var wireless = Delete(connection, transaction,
                "DELETE FROM wireless_ap_history WHERE captured_at < $cutoff;", historyCutoff);
            var audit = Delete(connection, transaction,
                "DELETE FROM audit_log WHERE occurred_at < $cutoff;", auditCutoff);

            transaction.Commit();
            return (metrics, probes, wireless, audit);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode = WAL;";
                pragma.ExecuteNonQuery();
            }

            Migrations.Apply(connection, _logger);
            SchemaVersion = Migrations.CurrentVersion;
        }

        _logger.LogInformation("SQLite monitoring database ready at schema v{Version}", SchemaVersion);
    }

    private void SeedIfEmpty()
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow;

            if (Count(connection, transaction, "devices") == 0)
            {
                InsertDevice(connection, transaction, new ManagedDevice
                {
                    Id = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20101"),
                    Name = "Workstation — Design",
                    Hostname = "DESIGN-WS-014",
                    Address = "10.20.4.14",
                    Kind = DeviceKind.Workstation,
                    OperatingSystem = "Windows 11 Pro",
                    AgentVersion = "0.1.0",
                    LastSeen = now.AddMinutes(-2),
                    Status = HealthStatus.Online,
                    CpuPercent = 31,
                    MemoryPercent = 62,
                    DiskPercent = 71,
                    Tags = "design, floor-2"
                });
                InsertDevice(connection, transaction, new ManagedDevice
                {
                    Id = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20102"),
                    Name = "File Server",
                    Hostname = "FILESRV-01",
                    Address = "10.20.1.10",
                    Kind = DeviceKind.Server,
                    OperatingSystem = "Windows Server 2022",
                    AgentVersion = "0.1.0",
                    LastSeen = now.AddMinutes(-1),
                    Status = HealthStatus.Warning,
                    CpuPercent = 68,
                    MemoryPercent = 78,
                    DiskPercent = 84,
                    Tags = "production, storage"
                });
                InsertDevice(connection, transaction, new ManagedDevice
                {
                    Id = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20103"),
                    Name = "Reception iPad",
                    Hostname = "RECEPTION-IPAD-01",
                    Address = "10.20.3.21",
                    Kind = DeviceKind.Mobile,
                    OperatingSystem = "iPadOS",
                    AgentVersion = string.Empty,
                    LastSeen = now.AddHours(-3),
                    Status = HealthStatus.Offline,
                    Tags = "front-desk"
                });
            }

            if (Count(connection, transaction, "network_targets") == 0)
            {
                InsertTarget(connection, transaction, new NetworkTarget
                {
                    Id = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20201"),
                    Name = "Core gateway",
                    Address = "gateway.example.org",
                    Port = 443,
                    Protocol = "tcp",
                    Kind = DeviceKind.Router,
                    Enabled = false,
                    Status = HealthStatus.Unknown,
                    Notes = "Replace with an authorized production address"
                });
                InsertTarget(connection, transaction, new NetworkTarget
                {
                    Id = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20202"),
                    Name = "Core switch",
                    Address = "switch.example.org",
                    Port = 443,
                    Protocol = "tcp",
                    Kind = DeviceKind.Switch,
                    Enabled = false,
                    Status = HealthStatus.Unknown,
                    Notes = "Replace with an authorized production address"
                });
                InsertTarget(connection, transaction, new NetworkTarget
                {
                    Id = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20203"),
                    Name = "Wireless controller",
                    Address = "wifi.example.org",
                    Port = 443,
                    Protocol = "https",
                    Kind = DeviceKind.Controller,
                    Enabled = false,
                    Status = HealthStatus.Unknown,
                    Notes = "Push inventory via POST /api/wireless/ingest in production"
                });
            }

            if (Count(connection, transaction, "alerts") == 0)
            {
                InsertAlert(connection, transaction, new MonitorAlert
                {
                    Id = Guid.NewGuid(),
                    Severity = AlertSeverity.warning,
                    Title = "File server disk usage is high",
                    Message = "Disk usage on FILESRV-01 is above the configured 85% warning threshold.",
                    DeviceId = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20102"),
                    EntityName = "File Server",
                    CreatedAt = now.AddMinutes(-18)
                });
            }

            if (Count(connection, transaction, "wireless_aps") == 0)
            {
                SeedAccessPoint(connection, transaction, new WirelessAccessPoint
                {
                    Id = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20301"),
                    Name = "AP-Lobby",
                    Site = "HQ / Ground floor",
                    Band = "2.4 GHz",
                    Channel = 6,
                    Clients = 18,
                    UtilizationPercent = 42,
                    NoiseFloorDbm = -92,
                    SnrDb = 38,
                    Firmware = "8.7.2",
                    Controller = "WLC-01",
                    Status = HealthStatus.Online,
                    LastSeen = now.AddMinutes(-1)
                });
                SeedAccessPoint(connection, transaction, new WirelessAccessPoint
                {
                    Id = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20302"),
                    Name = "AP-OpenOffice",
                    Site = "HQ / Floor 2",
                    Band = "5 GHz",
                    Channel = 44,
                    Clients = 46,
                    UtilizationPercent = 61,
                    NoiseFloorDbm = -95,
                    SnrDb = 41,
                    Firmware = "8.7.2",
                    Controller = "WLC-01",
                    Status = HealthStatus.Online,
                    LastSeen = now.AddMinutes(-1)
                });
                SeedAccessPoint(connection, transaction, new WirelessAccessPoint
                {
                    Id = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20303"),
                    Name = "AP-Warehouse",
                    Site = "Warehouse",
                    Band = "2.4 GHz",
                    Channel = 11,
                    Clients = 7,
                    UtilizationPercent = 88,
                    NoiseFloorDbm = -89,
                    SnrDb = 26,
                    Firmware = "8.6.9",
                    Controller = "WLC-01",
                    Status = HealthStatus.Warning,
                    LastSeen = now.AddMinutes(-2)
                });
                SeedAccessPoint(connection, transaction, new WirelessAccessPoint
                {
                    Id = Guid.Parse("7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20304"),
                    Name = "AP-MeetingRooms",
                    Site = "HQ / Floor 3",
                    Band = "6 GHz",
                    Channel = 37,
                    Clients = 12,
                    UtilizationPercent = 28,
                    NoiseFloorDbm = -96,
                    SnrDb = 44,
                    Firmware = "8.7.2",
                    Controller = "WLC-01",
                    Status = HealthStatus.Online,
                    LastSeen = now.AddMinutes(-1)
                });
            }

            transaction.Commit();
            _logger.LogInformation("Seeded demo monitoring data");
        }
    }

    private static long Count(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void InsertDevice(SqliteConnection connection, SqliteTransaction transaction, ManagedDevice device)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO devices
                (id, name, hostname, address, kind, operating_system, agent_version,
                 last_seen, status, cpu_percent, memory_percent, disk_percent, tags)
            VALUES
                ($id, $name, $hostname, $address, $kind, $operating_system, $agent_version,
                 $last_seen, $status, $cpu_percent, $memory_percent, $disk_percent, $tags);
            """;
        AddText(command, "$id", device.Id.ToString());
        AddText(command, "$name", device.Name);
        AddText(command, "$hostname", device.Hostname);
        AddText(command, "$address", device.Address);
        command.Parameters.AddWithValue("$kind", (int)device.Kind);
        AddText(command, "$operating_system", device.OperatingSystem);
        AddText(command, "$agent_version", device.AgentVersion);
        AddText(command, "$last_seen", device.LastSeen?.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", (int)device.Status);
        AddNullableDouble(command, "$cpu_percent", device.CpuPercent);
        AddNullableDouble(command, "$memory_percent", device.MemoryPercent);
        AddNullableDouble(command, "$disk_percent", device.DiskPercent);
        AddText(command, "$tags", device.Tags);
        command.ExecuteNonQuery();
    }

    private static void InsertTarget(SqliteConnection connection, SqliteTransaction transaction, NetworkTarget target)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO network_targets
                (id, name, address, port, protocol, kind, enabled, interval_seconds,
                 status, latency_ms, last_checked, last_error, notes)
            VALUES
                ($id, $name, $address, $port, $protocol, $kind, $enabled, $interval_seconds,
                 $status, $latency_ms, $last_checked, NULL, $notes);
            """;
        AddText(command, "$id", target.Id.ToString());
        AddText(command, "$name", target.Name);
        AddText(command, "$address", target.Address);
        command.Parameters.AddWithValue("$port", target.Port);
        AddText(command, "$protocol", target.Protocol);
        command.Parameters.AddWithValue("$kind", (int)target.Kind);
        command.Parameters.AddWithValue("$enabled", target.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$interval_seconds", target.IntervalSeconds);
        command.Parameters.AddWithValue("$status", (int)target.Status);
        AddNullableDouble(command, "$latency_ms", target.LatencyMs);
        AddText(command, "$last_checked", target.LastChecked?.ToString("O", CultureInfo.InvariantCulture));
        AddText(command, "$notes", target.Notes);
        command.ExecuteNonQuery();
    }

    private static void InsertAlert(SqliteConnection connection, SqliteTransaction transaction, MonitorAlert alert)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO alerts
                (id, severity, title, message, device_id, target_id, rule_id, dedupe_key,
                 device_name, created_at, acknowledged)
            VALUES
                ($id, $severity, $title, $message, $device_id, $target_id, $rule_id, $dedupe_key,
                 $device_name, $created_at, $acknowledged);
            """;
        AddText(command, "$id", alert.Id.ToString());
        AddText(command, "$severity", alert.Severity.ToString());
        AddText(command, "$title", alert.Title);
        AddText(command, "$message", alert.Message);
        AddText(command, "$device_id", alert.DeviceId?.ToString());
        AddText(command, "$target_id", alert.TargetId?.ToString());
        AddText(command, "$rule_id", alert.RuleId?.ToString());
        AddText(command, "$dedupe_key", null);
        AddText(command, "$device_name", alert.EntityName);
        AddText(command, "$created_at", alert.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$acknowledged", alert.Acknowledged ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void SeedAccessPoint(SqliteConnection connection, SqliteTransaction transaction, WirelessAccessPoint accessPoint)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO wireless_aps
                (id, name, site, band, channel, clients, utilization_percent, noise_floor_dbm,
                 snr_db, firmware, controller, status, last_seen, notes)
            VALUES
                ($id, $name, $site, $band, $channel, $clients, $utilization, $noise,
                 $snr, $firmware, $controller, $status, $last_seen, $notes);
            """;
        AddText(command, "$id", accessPoint.Id.ToString());
        AddText(command, "$name", accessPoint.Name);
        AddText(command, "$site", accessPoint.Site);
        AddText(command, "$band", accessPoint.Band);
        command.Parameters.AddWithValue("$channel", accessPoint.Channel);
        command.Parameters.AddWithValue("$clients", (object?)accessPoint.Clients ?? DBNull.Value);
        AddNullableDouble(command, "$utilization", accessPoint.UtilizationPercent);
        AddNullableDouble(command, "$noise", accessPoint.NoiseFloorDbm);
        AddNullableDouble(command, "$snr", accessPoint.SnrDb);
        AddText(command, "$firmware", accessPoint.Firmware);
        AddText(command, "$controller", accessPoint.Controller);
        command.Parameters.AddWithValue("$status", (int)accessPoint.Status);
        AddText(command, "$last_seen", accessPoint.LastSeen.ToString("O", CultureInfo.InvariantCulture));
        AddText(command, "$notes", accessPoint.Notes);
        command.ExecuteNonQuery();
    }

    // ---- shared row helpers -------------------------------------------------

    private static ManagedDevice ReadDevice(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Hostname = reader.GetString(2),
        Address = reader.GetString(3),
        Kind = (DeviceKind)reader.GetInt32(4),
        OperatingSystem = reader.GetString(5),
        AgentVersion = reader.GetString(6),
        LastSeen = ReadDate(reader, 7),
        Status = (HealthStatus)reader.GetInt32(8),
        CpuPercent = ReadDouble(reader, 9),
        MemoryPercent = ReadDouble(reader, 10),
        DiskPercent = ReadDouble(reader, 11),
        Tags = reader.GetString(12)
    };

    private static NetworkTarget ReadTarget(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Address = reader.GetString(2),
        Port = reader.GetInt32(3),
        Protocol = reader.GetString(4),
        Kind = (DeviceKind)reader.GetInt32(5),
        Enabled = reader.GetInt32(6) != 0,
        Status = (HealthStatus)reader.GetInt32(7),
        LatencyMs = ReadDouble(reader, 8),
        LastChecked = ReadDate(reader, 9),
        Notes = reader.GetString(10),
        IntervalSeconds = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetInt32(11) : 60,
        LastError = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null
    };

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces);

    private static double? ReadDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static string FirstNonEmpty(string? preferred, string? fallback, string defaultValue)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback.Trim();
        }

        return defaultValue;
    }

    private static void AddText(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, value ?? (object)DBNull.Value);

    private static void AddNullableDouble(SqliteCommand command, string name, double? value) =>
        command.Parameters.AddWithValue(name, value ?? (object)DBNull.Value);

    private static void AddGuid(SqliteCommand command, string name, Guid value) =>
        command.Parameters.AddWithValue(name, value.ToString());

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
