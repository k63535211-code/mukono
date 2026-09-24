using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace OrgMonitor.Api.Services.Database;

/// <summary>
/// Ordered, forward-only SQLite schema migrations.
/// Never edit an applied migration: add a new entry instead.
/// </summary>
public static class Migrations
{
    public sealed record Migration(int Version, string Sql);

    public static IReadOnlyList<Migration> All { get; } = new[]
    {
        new Migration(1, V1),
        new Migration(2, V2),
        new Migration(3, V3),
        new Migration(4, V4)
    };

    public static int CurrentVersion => All[^1].Version;

    public static void Apply(SqliteConnection connection, ILogger logger)
    {
        using (var bootstrap = connection.CreateCommand())
        {
            bootstrap.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY NOT NULL,
                    applied_at TEXT NOT NULL
                );
                """;
            bootstrap.ExecuteNonQuery();
        }

        var current = ReadVersion(connection);

        // Databases created before migrations existed already contain the v1 tables.
        if (current == 0 && TableExists(connection, "devices"))
        {
            WriteVersion(connection, 1);
            current = 1;
            logger.LogInformation("Baselined pre-migration database at schema version 1");
        }

        foreach (var migration in All)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();

                var version = migration.Version;
                using var record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_migrations (version, applied_at) VALUES ($version, $applied_at);";
                record.Parameters.AddWithValue("$version", version);
                record.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"));
                record.ExecuteNonQuery();

                transaction.Commit();
                logger.LogInformation("Applied database migration v{Version}", migration.Version);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    private static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO schema_migrations (version, applied_at) VALUES ($version, $applied_at);";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private const string V1 = """
        CREATE TABLE IF NOT EXISTS devices (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL,
            hostname TEXT NOT NULL,
            address TEXT NOT NULL,
            kind INTEGER NOT NULL,
            operating_system TEXT NOT NULL,
            agent_version TEXT NOT NULL,
            last_seen TEXT NULL,
            status INTEGER NOT NULL,
            cpu_percent REAL NULL,
            memory_percent REAL NULL,
            disk_percent REAL NULL,
            tags TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS network_targets (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL,
            address TEXT NOT NULL,
            port INTEGER NOT NULL,
            protocol TEXT NOT NULL,
            kind INTEGER NOT NULL,
            enabled INTEGER NOT NULL,
            status INTEGER NOT NULL,
            latency_ms REAL NULL,
            last_checked TEXT NULL,
            notes TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS alerts (
            id TEXT PRIMARY KEY NOT NULL,
            severity TEXT NOT NULL,
            title TEXT NOT NULL,
            message TEXT NOT NULL,
            device_id TEXT NULL,
            device_name TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            acknowledged INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_devices_status ON devices(status);
        CREATE INDEX IF NOT EXISTS ix_network_targets_enabled ON network_targets(enabled);
        CREATE INDEX IF NOT EXISTS ix_alerts_acknowledged_created ON alerts(acknowledged, created_at DESC);
        """;

    private const string V2 = """
        ALTER TABLE alerts ADD COLUMN resolved_at TEXT NULL;
        ALTER TABLE alerts ADD COLUMN dedupe_key TEXT NULL;
        ALTER TABLE alerts ADD COLUMN rule_id TEXT NULL;
        ALTER TABLE alerts ADD COLUMN target_id TEXT NULL;
        ALTER TABLE alerts ADD COLUMN acknowledged_by TEXT NULL;
        ALTER TABLE alerts ADD COLUMN acknowledged_at TEXT NULL;

        ALTER TABLE network_targets ADD COLUMN interval_seconds INTEGER NOT NULL DEFAULT 60;
        ALTER TABLE network_targets ADD COLUMN last_error TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_alerts_resolved_created ON alerts(resolved_at, created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_alerts_dedupe ON alerts(dedupe_key) WHERE dedupe_key IS NOT NULL;

        CREATE TABLE IF NOT EXISTS users (
            id TEXT PRIMARY KEY NOT NULL,
            username TEXT NOT NULL,
            password_hash TEXT NOT NULL,
            role INTEGER NOT NULL,
            active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            last_login_at TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_users_username ON users(username COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS audit_log (
            id TEXT PRIMARY KEY NOT NULL,
            occurred_at TEXT NOT NULL,
            actor_user_id TEXT NULL,
            actor_name TEXT NOT NULL DEFAULT '',
            action TEXT NOT NULL,
            entity_type TEXT NOT NULL DEFAULT '',
            entity_id TEXT NOT NULL DEFAULT '',
            details TEXT NOT NULL DEFAULT '',
            ip_address TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_audit_occurred ON audit_log(occurred_at DESC);

        CREATE TABLE IF NOT EXISTS device_metrics (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            device_id TEXT NOT NULL,
            captured_at TEXT NOT NULL,
            cpu_percent REAL NULL,
            memory_percent REAL NULL,
            disk_percent REAL NULL,
            FOREIGN KEY(device_id) REFERENCES devices(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_device_metrics_device_captured ON device_metrics(device_id, captured_at DESC);

        CREATE TABLE IF NOT EXISTS probe_results (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            target_id TEXT NOT NULL,
            checked_at TEXT NOT NULL,
            status INTEGER NOT NULL,
            latency_ms REAL NULL,
            error TEXT NULL,
            FOREIGN KEY(target_id) REFERENCES network_targets(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_probe_results_target_checked ON probe_results(target_id, checked_at DESC);

        CREATE TABLE IF NOT EXISTS alert_rules (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL,
            metric INTEGER NOT NULL,
            comparator INTEGER NOT NULL,
            threshold REAL NOT NULL,
            severity INTEGER NOT NULL,
            scope_kind INTEGER NULL,
            consecutive_breaches INTEGER NOT NULL DEFAULT 1,
            enabled INTEGER NOT NULL DEFAULT 1,
            description TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_alert_rules_metric ON alert_rules(metric, enabled);

        CREATE TABLE IF NOT EXISTS maintenance_windows (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL,
            scope INTEGER NOT NULL,
            scope_id TEXT NULL,
            starts_at TEXT NOT NULL,
            ends_at TEXT NOT NULL,
            reason TEXT NOT NULL DEFAULT '',
            created_by TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_maintenance_time ON maintenance_windows(starts_at, ends_at);

        CREATE TABLE IF NOT EXISTS notification_channels (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL,
            type INTEGER NOT NULL,
            url TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            min_severity INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            last_delivery_at TEXT NULL,
            last_success INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS notification_deliveries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id TEXT NOT NULL,
            alert_id TEXT NULL,
            attempted_at TEXT NOT NULL,
            success INTEGER NOT NULL,
            status_code INTEGER NULL,
            error TEXT NULL,
            FOREIGN KEY(channel_id) REFERENCES notification_channels(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_notification_deliveries_attempted ON notification_deliveries(attempted_at DESC);

        CREATE TABLE IF NOT EXISTS wireless_aps (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL,
            site TEXT NOT NULL DEFAULT '',
            band TEXT NOT NULL DEFAULT '',
            channel INTEGER NOT NULL DEFAULT 0,
            clients INTEGER NULL,
            utilization_percent REAL NULL,
            noise_floor_dbm REAL NULL,
            snr_db REAL NULL,
            firmware TEXT NOT NULL DEFAULT '',
            controller TEXT NOT NULL DEFAULT '',
            status INTEGER NOT NULL,
            last_seen TEXT NOT NULL,
            notes TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS ix_wireless_aps_status ON wireless_aps(status);

        CREATE TABLE IF NOT EXISTS wireless_ap_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ap_id TEXT NOT NULL,
            captured_at TEXT NOT NULL,
            clients INTEGER NULL,
            utilization_percent REAL NULL,
            noise_floor_dbm REAL NULL,
            snr_db REAL NULL,
            FOREIGN KEY(ap_id) REFERENCES wireless_aps(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_wireless_history_ap_captured ON wireless_ap_history(ap_id, captured_at DESC);

        INSERT OR IGNORE INTO alert_rules
            (id, name, metric, comparator, threshold, severity, scope_kind, consecutive_breaches, enabled, description, created_at)
        VALUES
            ('a1000000-0000-4000-8000-000000000001', 'High disk usage', 2, 1, 85, 1, NULL, 1, 1,
             'Disk usage at or above 85%.', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            ('a1000000-0000-4000-8000-000000000002', 'High memory usage', 1, 1, 90, 1, NULL, 1, 1,
             'Memory usage at or above 90%.', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            ('a1000000-0000-4000-8000-000000000003', 'Sustained high CPU', 0, 1, 92, 2, NULL, 2, 1,
             'CPU at or above 92% across two consecutive samples.', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            ('a1000000-0000-4000-8000-000000000004', 'Device stopped reporting', 3, 1, 0, 2, NULL, 1, 1,
             'Managed device missed the heartbeat offline threshold.', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            ('a1000000-0000-4000-8000-000000000005', 'Target unreachable', 4, 1, 0, 2, NULL, 1, 1,
             'Configured network probe could not reach the target.', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            ('a1000000-0000-4000-8000-000000000006', 'Slow network target', 5, 1, 750, 1, NULL, 3, 1,
             'Probe latency at or above 750ms across three checks.', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
        """;

    private const string V3 = """
        CREATE TABLE IF NOT EXISTS sessions (
            token_hash TEXT PRIMARY KEY NOT NULL,
            user_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_sessions_expires ON sessions(expires_at);
        """;

    private const string V4 = """
        ALTER TABLE users ADD COLUMN email TEXT NULL;
        ALTER TABLE users ADD COLUMN failed_attempts INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE users ADD COLUMN locked_until TEXT NULL;
        ALTER TABLE users ADD COLUMN must_change_password INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE IF NOT EXISTS password_resets (
            token_hash TEXT PRIMARY KEY NOT NULL,
            user_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            used_at TEXT NULL,
            delivery TEXT NOT NULL DEFAULT '',
            requested_by TEXT NOT NULL DEFAULT '',
            requested_ip TEXT NULL,
            FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_password_resets_user ON password_resets(user_id, created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_password_resets_expires ON password_resets(expires_at);
        """;
}
